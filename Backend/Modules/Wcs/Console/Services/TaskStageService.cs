using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Mapster;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Realtime;

namespace WCSBackend.Modules.Wcs.Console.Services;

/// <summary>task_records 是任务看板唯一数据源；内存仅保存当前进程的 FINISHED 等待器。</summary>
public interface ITaskStageService
{
    void Record(TaskStageChangeModel change);
    bool TryRecordSystemEvent(string taskId, string stage, bool ok, int statusCode = 0,
        string? stationCode = null, string? containerCode = null, string? cargoCode = null);
    void RecordDispatchResult(string taskId, bool ok, int statusCode);
    List<StageChangeEvent> GetEventsSince(long sinceId, int limit = 1000);
    void RecordCreated(List<TaskLedgerEntry> entries);
    List<TaskLedgerEntry> GetCreated(int limit = 500);
    List<TaskRecord> GetAll();
    TaskRecord? GetById(long id);
    void RemoveByTaskId(string taskId);
    void ClearAll();
    HashSet<string> FinishedTaskIds { get; }
    event Action<string>? TaskFinished;
    event Action<string>? TaskLoadFinished;
    Task WaitFinishedAsync(string taskId, TimeSpan? timeout = null);
    void ForceCompleteAll();
}

public class TaskStageService : ITaskStageService
{
    private readonly object _writeLock = new();
    private readonly object _waitLock = new();
    private readonly IHubContext<TaskStageRealtimeHub> _hub;
    private readonly IUnitOfWorkFactory _uow;
    private readonly TaskLifecycleService _lifecycle;
    private readonly HashSet<string> _forcedFinished = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxRecords = 10000;

    public TaskStageService(IHubContext<TaskStageRealtimeHub> hub, IUnitOfWorkFactory uowFactory,
        TaskLifecycleService lifecycle)
    {
        _hub = hub;
        _uow = uowFactory;
        _lifecycle = lifecycle;
        _lifecycle.Seed(GetAll());
    }

    public HashSet<string> FinishedTaskIds
    {
        get
        {
            using var uow = _uow.Create();
            return uow.Repository<TaskRecord>().Query()
                .Where(record => record.Stage == "FINISHED" && record.TaskId != "")
                .Select(record => record.TaskId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public event Action<string>? TaskFinished;
    public event Action<string>? TaskLoadFinished;

    public void RecordCreated(List<TaskLedgerEntry> entries)
    {
        if (entries == null || entries.Count == 0) return;
        var added = new List<TaskRecord>();
        lock (_writeLock)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.TaskId) || HasCreatedRecord(entry.TaskId)) continue;
                var record = entry.Adapt<TaskRecord>();
                record.StageStatus = TaskStageStatuses.Success;
                InsertRecord(record);
                added.Add(record);
            }
        }
        foreach (var record in added)
            _ = _hub.Clients.All.SendAsync("EventAdded", record);
    }

    public bool TryRecordSystemEvent(string taskId, string stage, bool ok, int statusCode = 0,
        string? stationCode = null, string? containerCode = null, string? cargoCode = null)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(stage)) return false;
        TaskRecord? record = null;
        lock (_writeLock)
        {
            if (HasStage(taskId, stage)) return false;
            var created = FindCreated(taskId);
            record = new TaskRecord
            {
                TaskId = taskId,
                Stage = stage,
                Time = DateTime.Now,
                Warehouse = created?.Warehouse ?? "",
                ContainerCode = containerCode ?? created?.ContainerCode ?? "",
                CargoCode = cargoCode ?? created?.CargoCode ?? "",
                TaskType = created?.TaskType ?? "",
                StartStationCode = created?.StartStationCode ?? "",
                EndStationCode = created?.EndStationCode ?? "",
                StageStatus = ok ? TaskStageStatuses.Success : TaskStageStatuses.Fail,
                StatusCode = statusCode,
            };
            InsertRecord(record);
        }
        ApplySystemLifecycle(taskId, stage, ok, statusCode);
        _ = _hub.Clients.All.SendAsync("EventAdded", record);
        return true;
    }

    public void RecordDispatchResult(string taskId, bool ok, int statusCode)
    {
        lock (_writeLock)
        {
            var created = FindCreated(taskId);
            if (created != null)
            {
                created.StageStatus = ok ? TaskStageStatuses.Success : TaskStageStatuses.Fail;
                created.StatusCode = statusCode;
                UpdateRecord(created);
            }
        }
        TryRecordSystemEvent(taskId, ok ? "DISPATCHED" : "DISPATCH_FAILED", ok, statusCode);
        _ = _hub.Clients.All.SendAsync("EventsReset", GetAll());
    }

    public void Record(TaskStageChangeModel change)
    {
        if (string.IsNullOrWhiteSpace(change.TaskId) || string.IsNullOrWhiteSpace(change.Stage)) return;
        TaskRecord? record = null;
        lock (_writeLock)
        {
            if (HasStageAt(change.TaskId, change.Stage, change.MsgTime)) return;
            var created = FindCreated(change.TaskId);
            record = new TaskRecord
            {
                TaskId = change.TaskId,
                Stage = change.Stage,
                Time = change.MsgTime,
                Warehouse = string.IsNullOrWhiteSpace(change.Warehouse) ? created?.Warehouse ?? "" : change.Warehouse,
                ContainerCode = string.IsNullOrWhiteSpace(change.ContainerCode) ? created?.ContainerCode ?? "" : change.ContainerCode,
                TaskType = created?.TaskType ?? "",
                CargoCode = created?.CargoCode ?? "",
                StartStationCode = created?.StartStationCode ?? "",
                EndStationCode = created?.EndStationCode ?? "",
                StageStatus = TaskStageStatuses.Callback,
                StatusCode = 0,
            };
            InsertRecord(record);
        }

        var transition = _lifecycle.ApplyStage(change.TaskId, change.Stage);
        TaskCompletionSource<bool>? finishedWaiter = null;
        var raiseFinished = transition.Changed && transition.Current == WcsTaskState.Finished;
        var raiseLoadFinished = transition.Changed && transition.Current == WcsTaskState.LoadFinished;
        if (raiseFinished)
        {
            lock (_waitLock)
            {
                _forcedFinished.Add(change.TaskId);
                _waiters.Remove(change.TaskId, out finishedWaiter);
            }
        }

        _ = _hub.Clients.All.SendAsync("EventAdded", record);
        finishedWaiter?.TrySetResult(true);
        if (raiseLoadFinished) TaskLoadFinished?.Invoke(change.TaskId);
        if (raiseFinished) TaskFinished?.Invoke(change.TaskId);
    }

    public List<StageChangeEvent> GetEventsSince(long sinceId, int limit = 1000)
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().Query()
            .Where(record => record.Stage != "CREATED" && record.Id > sinceId)
            .OrderBy(record => record.Id).Take(limit).ToList()
            .Select(record => record.Adapt<StageChangeEvent>()).ToList();
    }

    public List<TaskLedgerEntry> GetCreated(int limit = 500)
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().Query()
            .Where(record => record.Stage == "CREATED")
            .OrderByDescending(record => record.Id).Take(limit).ToList()
            .Select(record => record.Adapt<TaskLedgerEntry>()).ToList();
    }

    public List<TaskRecord> GetAll()
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().Query().OrderBy(record => record.Id).ToList();
    }

    public TaskRecord? GetById(long id)
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().FindAsync(id).GetAwaiter().GetResult();
    }

    public void RemoveByTaskId(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        lock (_writeLock) RemoveByTaskIdDb(taskId);
        lock (_waitLock)
        {
            _forcedFinished.Remove(taskId);
            _waiters.Remove(taskId, out _);
        }
        _lifecycle.Remove(taskId);
        _ = _hub.Clients.All.SendAsync("TaskRemoved", taskId);
    }

    public void ClearAll()
    {
        lock (_writeLock) ClearAllDb();
        lock (_waitLock)
        {
            _forcedFinished.Clear();
            _waiters.Clear();
        }
        _lifecycle.Clear();
        _ = _hub.Clients.All.SendAsync("EventsReset", new List<TaskRecord>());
    }

    public void ForceCompleteAll()
    {
        List<TaskCompletionSource<bool>> pending;
        lock (_waitLock)
        {
            pending = _waiters.Values.ToList();
            foreach (var taskId in _waiters.Keys) _forcedFinished.Add(taskId);
            _waiters.Clear();
        }
        foreach (var waiter in pending) waiter.TrySetResult(true);
    }

    public Task WaitFinishedAsync(string taskId, TimeSpan? timeout = null)
    {
        TaskCompletionSource<bool> waiter;
        lock (_waitLock)
        {
            if (_forcedFinished.Contains(taskId) || HasStage(taskId, "FINISHED")
                || _lifecycle.GetState(taskId) == WcsTaskState.Finished)
                return Task.CompletedTask;
            if (!_waiters.TryGetValue(taskId, out waiter!))
            {
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[taskId] = waiter;
            }
        }
        return timeout is { } value && value > TimeSpan.Zero ? waiter.Task.WaitAsync(value) : waiter.Task;
    }

    private bool HasCreatedRecord(string taskId)
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().Query().Any(record => record.TaskId == taskId && record.Stage == "CREATED");
    }

    private TaskRecord? FindCreated(string taskId)
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().Query()
            .Where(record => record.TaskId == taskId && record.Stage == "CREATED")
            .OrderBy(record => record.Id).FirstOrDefault();
    }

    private bool HasStage(string taskId, string stage)
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().Query().Any(record => record.TaskId == taskId && record.Stage == stage);
    }

    private bool HasStageAt(string taskId, string stage, DateTime time)
    {
        using var uow = _uow.Create();
        return uow.Repository<TaskRecord>().Query()
            .Any(record => record.TaskId == taskId && record.Stage == stage && record.Time == time);
    }

    private void InsertRecord(TaskRecord record)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<TaskRecord>();
        repo.AddAsync(record).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        var keep = repo.Query().OrderByDescending(item => item.Id).Take(MaxRecords).Select(item => item.Id).ToList();
        if (keep.Count == MaxRecords)
        {
            repo.DeleteWhereAsync(item => !keep.Contains(item.Id)).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    private void UpdateRecord(TaskRecord record)
    {
        using var uow = _uow.Create();
        var persisted = uow.Repository<TaskRecord>().FindAsync(record.Id).GetAwaiter().GetResult();
        if (persisted == null) return;
        persisted.StageStatus = record.StageStatus;
        persisted.StatusCode = record.StatusCode;
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private void ApplySystemLifecycle(string taskId, string stage, bool ok, int statusCode)
    {
        if (string.Equals(stage, "DISPATCHED", StringComparison.OrdinalIgnoreCase) && ok)
            _lifecycle.DispatchAccepted(taskId);
        else if (string.Equals(stage, "DISPATCH_FAILED", StringComparison.OrdinalIgnoreCase))
            _lifecycle.DispatchFailed(taskId, statusCode == 0);
        else if (string.Equals(stage, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            _lifecycle.Cancel(taskId);
    }

    private void RemoveByTaskIdDb(string taskId)
    {
        using var uow = _uow.Create();
        uow.Repository<TaskRecord>().DeleteWhereAsync(record => record.TaskId == taskId).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private void ClearAllDb()
    {
        using var uow = _uow.Create();
        uow.Repository<TaskRecord>().DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }
}
