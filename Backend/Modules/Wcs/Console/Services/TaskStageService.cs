using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Realtime;
using WCSBackend.Modules.Wcs.Automation.Services;
using Mapster;
using Microsoft.AspNetCore.SignalR;

namespace WCSBackend.Modules.Wcs.Console.Services;

/// <summary>
/// 任务记录统一服务：合并后的 task_records 表（创建行 CREATED + 阶段行 START/LOAD_FINISH/FINISHED）。
/// GRCS 回调 task_stage_change 写阶段行，WCS 下发（LedgerStore.AppendAsync）写创建行；
/// 两者都落同一张表并 SignalR 广播，前端 TaskStageHub 全表缓存后自行按 stage 筛选。
/// 状态必须跨请求共享（GRCS 上报 + 前端查询），用 Singleton 注册。
///
/// ── Id 契约（前端 TaskStageHub 依赖）──
/// 每条记录有 SQLite 自增 Id（TaskRecordInsert 分配，进程重启从表恢复后继续）。
/// 前端增量轮询以 sinceId 为水位只取新事件（阶段视图）；创建行与阶段行共享 Id 空间。
///
/// ── 容量边界 ──
/// MaxRecords 兜底上限 10000：超过时丢弃最旧记录（RemoveRange 前段）。
/// GetEvents 默认只回最近 200 条（全量首拉），GetEventsSince 上限 1000 条。
/// </summary>
public interface ITaskStageService
{
    void Record(TaskStageChangeModel change);
    /// <summary>记录 WCS 自己产生的、每任务每阶段只允许一次的业务事件。</summary>
    bool TryRecordSystemEvent(string taskId, string stage, bool ok, int statusCode = 0,
        string? stationCode = null, string? containerCode = null, string? cargoCode = null);
    /// <summary>标记任务已被 GRCS 接收，同时更新 CREATED 行的下发结果。</summary>
    void RecordDispatchResult(string taskId, bool ok, int statusCode);
    /// <summary>增量查询：只返回 Id 大于 sinceId 的阶段事件（前端轮询收敛用）。</summary>
    List<StageChangeEvent> GetEventsSince(long sinceId, int limit = 1000);
    /// <summary>写创建行（stage=CREATED，来自下发台账；同一任务只写一条，重复调用跳过）。</summary>
    void RecordCreated(List<TaskLedgerEntry> entries);
    /// <summary>读创建行（投影为台账条目，id 倒序）。</summary>
    List<TaskLedgerEntry> GetCreated(int limit = 500);
    /// <summary>全表（创建行 + 阶段行，id 升序）供 SignalR 快照回放。</summary>
    List<TaskRecord> GetAll();
    TaskRecord? GetById(long id);
    void RemoveByTaskId(string taskId);
    /// <summary>清空全表（创建行 + 阶段行）并广播 EventsReset 空快照。</summary>
    void ClearAll();

    /// <summary>已到达 FINISHED 的任务号集合（大小写不敏感；自动化解锁/信号放行用）。</summary>
    HashSet<string> FinishedTaskIds { get; }

    /// <summary>任务到达 FINISHED 时触发（参数 = taskId）；供后端模块执行器在终点阶段跑模块。</summary>
    event Action<string>? TaskFinished;

    /// <summary>任务到达 LOAD_FINISH（载货成功 = 已被取走）时触发（参数 = taskId）；供库存状态机推进 transit。</summary>
    event Action<string>? TaskLoadFinished;

    /// <summary>等待任务到达 FINISHED（进程内事件驱动，默认无限等待；传入 timeout 才会限时）。</summary>
    Task WaitFinishedAsync(string taskId, TimeSpan? timeout = null);

/// <summary>强制完成所有等待中的 FINISHED 等待器（用于强制结束）。</summary>
    void ForceCompleteAll();
}

public class TaskStageService : ITaskStageService
{
    private readonly object _lock = new();
    private readonly IHubContext<TaskStageRealtimeHub> _hub;
    private readonly IUnitOfWorkFactory _uow;
    private readonly TaskLifecycleService _lifecycle;
    private readonly List<TaskRecord> _records = [];   // 全表（创建行 + 阶段行，到达顺序）
    private readonly HashSet<string> _finished = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);   // 阶段行幂等：taskId|stage|timeTicks
    private readonly HashSet<string> _systemEventKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdTasks = new(StringComparer.OrdinalIgnoreCase);   // 已有创建行的任务（防重复创建）
    private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;
    private const int MaxRecords = 10000;
    private const int MaxFinished = 3000;

    public TaskStageService(IHubContext<TaskStageRealtimeHub> hub, IUnitOfWorkFactory uowFactory,
        TaskLifecycleService lifecycle)
    {
        _hub = hub;
        _uow = uowFactory;
        _lifecycle = lifecycle;
        // 启动时从 SQLite 全表恢复（重启不丢），并恢复 Id 水位、FINISHED 集合与已创建任务集
        List<TaskRecord> loaded;
        using (var uow = _uow.Create())
            loaded = uow.Repository<TaskRecord>().FindAllAsync().GetAwaiter().GetResult();
        foreach (var r in loaded)
        {
            _records.Add(r);
            if (r.IsCreated) _createdTasks.Add(r.TaskId);
            else _seenKeys.Add(DedupKey(r.TaskId, r.Stage, r.Time));
            if (IsSystemStage(r.Stage)) _systemEventKeys.Add(SystemEventKey(r.TaskId, r.Stage));
            if (string.Equals(r.Stage, "FINISHED", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.TaskId))
                _finished.Add(r.TaskId);
            if (r.Id >= _nextId) _nextId = r.Id + 1;
        }
        if (_records.Count > MaxRecords)
            _records.RemoveRange(0, _records.Count - MaxRecords);
        if (_finished.Count > MaxFinished) _finished.Clear();
        _lifecycle.Seed(loaded);
    }

    public HashSet<string> FinishedTaskIds
    {
        get { lock (_lock) { return new HashSet<string>(_finished, StringComparer.OrdinalIgnoreCase); } }
    }

    public event Action<string>? TaskFinished;

    public event Action<string>? TaskLoadFinished;

    /// <summary>写创建行（来自下发台账）：同一任务只保留一条 CREATED，重复写入跳过。</summary>
    public void RecordCreated(List<TaskLedgerEntry> entries)
    {
        if (entries == null || entries.Count == 0) return;
        var added = new List<TaskRecord>();
        lock (_lock)
        {
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.TaskId)) continue;
                if (!_createdTasks.Add(e.TaskId)) continue;
                var rec = e.Adapt<TaskRecord>();
                _records.Add(rec);
                added.Add(rec);
            }
            TrimRecordsLocked();
        }
        foreach (var rec in added)
        {
            var newId = InsertRecord(rec);   // 持久化（锁外 IO）
            lock (_lock) { rec.Id = newId; }
            _ = _hub.Clients.All.SendAsync("EventAdded", rec);   // 创建行同样实时广播（锁外发）
        }
    }

    public bool TryRecordSystemEvent(string taskId, string stage, bool ok, int statusCode = 0,
        string? stationCode = null, string? containerCode = null, string? cargoCode = null)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(stage)) return false;
        TaskRecord rec;
        lock (_lock)
        {
            var key = SystemEventKey(taskId, stage);
            if (!_systemEventKeys.Add(key)) return false;
            var created = _records.FirstOrDefault(r => r.IsCreated
                && string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            rec = new TaskRecord
            {
                TaskId = taskId,
                Stage = stage,
                Time = DateTime.Now,
                Warehouse = created?.Warehouse ?? "",
                StationCode = stationCode ?? "",
                ContainerCode = containerCode ?? created?.ContainerCode ?? "",
                CargoCode = cargoCode ?? created?.CargoCode ?? "",
                TaskType = created?.TaskType ?? "",
                RouteCodes = created?.RouteCodes.ToList() ?? [],
                Ok = ok,
                StatusCode = statusCode,
            };
            _records.Add(rec);
            TrimRecordsLocked();
        }
        var newId = InsertRecord(rec);
        lock (_lock) { rec.Id = newId; }
        ApplySystemLifecycle(taskId, stage, ok, statusCode);
        _ = _hub.Clients.All.SendAsync("EventAdded", rec);
        return true;
    }

    public void RecordDispatchResult(string taskId, bool ok, int statusCode)
    {
        TaskRecord? created;
        lock (_lock)
        {
            created = _records.FirstOrDefault(r => r.IsCreated
                && string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            if (created != null)
            {
                created.Ok = ok;
                created.StatusCode = statusCode;
            }
        }
        if (created != null) UpdateRecord(created);
        TryRecordSystemEvent(taskId, ok ? "DISPATCHED" : "DISPATCH_FAILED", ok, statusCode);
        // CREATED 行的下发结果是一次更新而非新行，推送快照让已打开的任务看板同步该更新。
        _ = _hub.Clients.All.SendAsync("EventsReset", GetAll());
    }

    public void Record(TaskStageChangeModel change)
    {
        // 幂等：GRCS 重发同一条（同任务同阶段同时刻）直接跳过，防流水重复
        var dedupKey = DedupKey(change.TaskId, change.Stage, change.MsgTime);
        TaskRecord rec;
        lock (_lock)
        {
            if (!_seenKeys.Add(dedupKey)) return;
            rec = new TaskRecord
            {
                TaskId = change.TaskId,
                Stage = change.Stage,
                Time = change.MsgTime,
                Warehouse = change.Warehouse,
                StationCode = change.StationCode,
                ContainerCode = change.ContainerCode,
                TaskType = "",
                RouteCodes = [],
                CargoCode = "",
                Ok = false,
                StatusCode = 0,
            };
            _records.Add(rec);
            TrimRecordsLocked();
        }
        var newId = InsertRecord(rec);   // 持久化（锁外 IO）
        lock (_lock) { rec.Id = newId; }

        // 状态转换与业务副作用分离：原始回调照常记流水，只有首次合法转换才触发事件。
        var transition = _lifecycle.ApplyStage(change.TaskId, change.Stage);
        TaskCompletionSource<bool>? finishedWaiter = null;
        var raiseFinished = transition.Changed && transition.Current == WcsTaskState.Finished;
        var raiseLoadFinished = transition.Changed && transition.Current == WcsTaskState.LoadFinished;
        if (raiseFinished)
        {
            lock (_lock)
            {
                _finished.Add(change.TaskId);
                if (_finished.Count > MaxFinished)
                    _finished.Clear(); // WaitFinishedAsync 仍可通过生命周期状态识别已完成任务
                _waiters.Remove(change.TaskId, out finishedWaiter);
            }
        }

        // 实时推送：新记录广播给所有已连接的 WCS 前端（锁外发，避免持锁做网络 IO）
        _ = _hub.Clients.All.SendAsync("EventAdded", rec);
        finishedWaiter?.TrySetResult(true);
        if (raiseLoadFinished) TaskLoadFinished?.Invoke(change.TaskId);
        if (raiseFinished) TaskFinished?.Invoke(change.TaskId);
    }

    public List<StageChangeEvent> GetEventsSince(long sinceId, int limit = 1000)
    {
        lock (_lock)
        {
            return _records.Where(r => !r.IsCreated && r.Id > sinceId).TakeLast(limit).Select(r => r.Adapt<StageChangeEvent>()).ToList();
        }
    }

    public List<TaskLedgerEntry> GetCreated(int limit = 500)
    {
        lock (_lock)
        {
            return _records.Where(r => r.IsCreated).TakeLast(limit).Reverse().Select(r => r.Adapt<TaskLedgerEntry>()).ToList();
        }
    }

    public List<TaskRecord> GetAll()
    {
        lock (_lock)
        {
            return _records.ToList();
        }
    }

    public TaskRecord? GetById(long id)
    {
        lock (_lock) return _records.FirstOrDefault(r => r.Id == id);
    }

    public void RemoveByTaskId(string taskId)
    {
        lock (_lock)
        {
            _records.RemoveAll(r => string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            _finished.Remove(taskId);
            _createdTasks.Remove(taskId);
            _seenKeys.RemoveWhere(k => k.StartsWith(taskId + "|", StringComparison.OrdinalIgnoreCase));
            _systemEventKeys.RemoveWhere(k => k.StartsWith(taskId + "|", StringComparison.OrdinalIgnoreCase));
        }
        _lifecycle.Remove(taskId);
        RemoveByTaskIdDb(taskId);   // 同步删库（全行：创建 + 阶段）
        // 实时推送：通知各标签页同步删除本地缓存
        _ = _hub.Clients.All.SendAsync("TaskRemoved", taskId);
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _records.Clear();
            _finished.Clear();
            _createdTasks.Clear();
            _seenKeys.Clear();
            _systemEventKeys.Clear();
        }
        _lifecycle.Clear();
        ClearAllDb();
        // 实时推送：空快照让各标签页整表替换为空
        _ = _hub.Clients.All.SendAsync("EventsReset", new List<TaskRecord>());
    }

    private void TrimRecordsLocked()
    {
        if (_records.Count > MaxRecords)
            _records.RemoveRange(0, _records.Count - MaxRecords);
    }

    /// <summary>持久化单条（EF 插入，自增 Id 回填；超过上限丢弃最旧记录）。</summary>
    private long InsertRecord(TaskRecord rec)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<TaskRecord>();
        repo.AddAsync(rec).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        if (rec.Id > MaxRecords)
        {
            var keep = repo.Query().OrderByDescending(r => r.Id).Take(MaxRecords).Select(r => r.Id).ToList();
            if (keep.Count > 0)
                repo.DeleteWhereAsync(r => !keep.Contains(r.Id)).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
        return rec.Id;
    }

    private void UpdateRecord(TaskRecord rec)
    {
        using var uow = _uow.Create();
        var persisted = uow.Repository<TaskRecord>().FindAsync(rec.Id).GetAwaiter().GetResult();
        if (persisted == null) return;
        persisted.Ok = rec.Ok;
        persisted.StatusCode = rec.StatusCode;
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

    private static bool IsSystemStage(string stage)
        => !string.Equals(stage, "CREATED", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(stage, "START", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(stage, "LOAD_FINISH", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(stage, "FINISHED", StringComparison.OrdinalIgnoreCase);

    private static string SystemEventKey(string taskId, string stage)
        => $"{taskId}|{stage}";

    /// <summary>删除某任务全部行（创建行 + 阶段行）。</summary>
    private void RemoveByTaskIdDb(string taskId)
    {
        using var uow = _uow.Create();
        uow.Repository<TaskRecord>().DeleteWhereAsync(r => r.TaskId == taskId).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>清空 task_records 全表。</summary>
    private void ClearAllDb()
    {
        using var uow = _uow.Create();
        uow.Repository<TaskRecord>().DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private static string DedupKey(string taskId, string stage, DateTime time)
        => $"{taskId}|{stage}|{time.Ticks}";

    public void ForceCompleteAll()
    {
        List<(string taskId, TaskCompletionSource<bool> tcs)> pending;
        lock (_lock)
        {
            pending = _waiters.Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (id, _) in pending) _finished.Add(id);
            _waiters.Clear();
        }
        foreach (var (_, tcs) in pending) tcs.TrySetResult(true);
    }

    public Task WaitFinishedAsync(string taskId, TimeSpan? timeout = null)
    {
        TaskCompletionSource<bool> tcs;
        lock (_lock)
        {
            if (_finished.Contains(taskId) || _lifecycle.GetState(taskId) == WcsTaskState.Finished)
                return Task.CompletedTask;
            if (_waiters.TryGetValue(taskId, out var existing)) tcs = existing;
            else
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters[taskId] = tcs;
            }
        }
        var wait = tcs.Task;
        var t = timeout ?? TimeSpan.Zero;
        if (t > TimeSpan.Zero) wait = wait.WaitAsync(t);
        return wait;
    }
}
