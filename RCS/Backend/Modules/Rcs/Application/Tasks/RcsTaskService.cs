using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Backend.Shared.Logging;
using Contracts.Rcs.Tasks;
using RCSBackend.Modules.Rcs.Application.Execution;
using RCSBackend.Modules.Rcs.Application.Scheduling;
using RCSBackend.Modules.Rcs.Application.Vehicles;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

/// <summary>任务接收、持久化和每车生命周期。长时间执行不占用调度锁。</summary>
public sealed class RcsTaskService(IRcsTaskStore store, RcsMapCache maps, IRcsTaskScheduler scheduler,
    IRcsTaskExecutionService executor, RcsVehicleRegistry vehicles, RcsDispatchLock dispatchLock,
    ILogger<RcsTaskService> logger) : IRcsTaskService
{
    private readonly SemaphoreSlim _gate = dispatchLock.Gate;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<string, ActiveTask> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Task> _executions = new();
    private CancellationToken _executionLifetime;
    public event Action<RcsTaskDto>? TaskChanged;
    public void NotifyWork() => _wake.Writer.TryWrite(true);
    public void SetExecutionLifetime(CancellationToken token) => _executionLifetime = token;
    public bool IsVehicleBusy(string vehicleId) => _active.ContainsKey(vehicleId);

    public Task<RcsTaskReceiveResponse> ReceiveAsync(RcsTaskReceiveRequest request, CancellationToken token = default) =>
        ReceiveFromAsync(request, RcsTaskSource.Upstream, token);

    public Task<RcsTaskReceiveResponse> ReceiveManualAsync(RcsTaskReceiveRequest request, CancellationToken token = default) =>
        ReceiveFromAsync(request, RcsTaskSource.Manual, token);

    private async Task<RcsTaskReceiveResponse> ReceiveFromAsync(RcsTaskReceiveRequest request, string source, CancellationToken token)
    {
        RcsTaskRequestValidator.Validate(request, allowManualTaskType: source == RcsTaskSource.Manual);
        await vehicles.InitializeAsync(token);
        await _gate.WaitAsync(token);
        try
        {
            var existing = await store.FindAsync(request.Tasks.Select(x => x.TaskId).ToArray(), token);
            var byId = existing.ToDictionary(x => x.TaskId, StringComparer.OrdinalIgnoreCase);
            var added = new List<RcsTaskRow>();
            var currentMap = maps.Current;
            foreach (var input in request.Tasks)
            {
                if (byId.TryGetValue(input.TaskId, out var duplicate))
                {
                    if (!SameRequest(duplicate, request, input, source))
                        throw new RcsTaskConflictException($"TaskId {input.TaskId} 已存在且报文内容不同。");
                    continue;
                }
                var map = currentMap ?? throw new ArgumentException("没有当前地图，请先发布并加载地图。");
                if (!string.Equals(map.SceneName, request.Warehouse, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"场景 {request.Warehouse} 与当前地图场景 {map.SceneName} 不一致。");
                if (input.StationCode.Any(code => !map.Points.ContainsKey(code)))
                    throw new ArgumentException($"任务 {input.TaskId} 的 StationCode 包含不存在或禁用的站点。");
                if (input.VehicleId != "")
                {
                    var target = executor.GetVehicles().FirstOrDefault(x => SameId(x.Id, input.VehicleId))
                        ?? throw new ArgumentException($"车辆 {input.VehicleId} 不存在。");
                    if (source == RcsTaskSource.Manual)
                    {
                        if (!target.IsEnabled) throw new ArgumentException($"车辆 {input.VehicleId} 已停用。");
                        if (target.OperatingMode != RcsOperatingMode.Manual)
                            throw new ArgumentException($"手动界面任务必须指定手动模式车辆，车辆 {input.VehicleId} 当前为自动模式。");
                    }
                }
                else if (source == RcsTaskSource.Manual)
                    throw new ArgumentException("手动界面任务必须指定一台手动模式车辆。");
                var row = new RcsTaskRow
                {
                    TaskId = input.TaskId, GroupId = request.GroupId, MsgTime = request.MsgTime,
                    Warehouse = request.Warehouse, PriorityCode = request.PriorityCode,
                    TaskType = input.TaskType, ContainerCode = input.ContainerCode, RequestedVehicleId = input.VehicleId,
                    Source = source,
                    StationCodesJson = JsonSerializer.Serialize(input.StationCode),
                    StationActionsJson = JsonSerializer.Serialize(input.StationActions),
                    AreaCodesJson = JsonSerializer.Serialize(input.AreaCode), MapCode = map.MapCode,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                };
                if (source == RcsTaskSource.Manual)
                {
                    var target = executor.GetVehicles().First(x => SameId(x.Id, input.VehicleId));
                    if (_active.ContainsKey(target.Id) || target.Status is not ("Idle" or "Arrived"))
                        throw new RcsTaskConflictException($"手动车辆 {target.Id} 当前忙碌，任务未下发。请等车辆空闲后重试。");
                    if (added.Any(x => SameId(x.VehicleId, target.Id)))
                        throw new ArgumentException($"同一批手动任务不能重复下发给车辆 {target.Id}。");
                    row.Status = RcsTaskStatus.Running;
                    row.VehicleId = target.Id;
                    row.StartedAt = DateTime.UtcNow;
                }
                added.Add(row); byId.Add(row.TaskId, row);
            }
            if (added.Count > 0) await store.AddAsync(added, token);
            foreach (var task in added)
            {
                if (source == RcsTaskSource.Manual) StartExecution(task, _executionLifetime, "手动任务已直接下发给指定车辆");
                else Publish(task, "任务已接收并进入等待队列");
            }
            if (source == RcsTaskSource.Upstream) NotifyWork();
            return new RcsTaskReceiveResponse { Success = true, MsgTime = ProtocolTime(),
                Message = source == RcsTaskSource.Manual
                    ? $"新增 {added.Count} 个手动任务，已直接下发给指定车辆；已存在 {existing.Count} 个任务。"
                    : $"新增 {added.Count} 个任务，已存在 {existing.Count} 个任务；任务已保存，等待自动车辆分配。",
                Tasks = request.Tasks.Select(x => byId[x.TaskId].ToDto()).ToArray() };
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RcsTaskDto>> ListAsync(int limit, CancellationToken token = default) =>
        (await store.ListAsync(Math.Clamp(limit, 1, 500), token)).Select(x => x.ToDto()).ToArray();
    public async Task<RcsTaskDto?> GetAsync(string taskId, CancellationToken token = default) =>
        (await store.FindAsync([taskId], token)).FirstOrDefault()?.ToDto();
    public Task RecoverAsync(CancellationToken token) => store.RecoverAsync(token);
    public async Task WaitForWorkAsync(CancellationToken token) => await _wake.Reader.ReadAsync(token);

    // 只领取并启动一个任务，立即返回，后台循环可继续给其他空闲车分配。
    public async Task<bool> DispatchNextAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken);
        try
        {
            var waiting = await store.WaitingCandidatesAsync(RcsTaskSource.Upstream, stoppingToken);
            var available = executor.GetVehicles().Where(x => !_active.ContainsKey(x.Id)).ToArray();
            var assignment = scheduler.Select(waiting, available);
            if (assignment is null) return false;
            if (_active.ContainsKey(assignment.VehicleId) || !available.Any(x => x.Id == assignment.VehicleId && x.IsEnabled
                && x.OperatingMode == RcsOperatingMode.Automatic && (x.Status is "Idle" or "Arrived")))
                throw new InvalidOperationException("调度器返回了不可用或非自动模式车辆。");
            var next = (await store.FindAsync([assignment.TaskId], stoppingToken)).FirstOrDefault();
            if (next is null) return false;
            if (next.Id != assignment.QueueId || next.Status != RcsTaskStatus.Waiting) return true;
            if (next.RequestedVehicleId != "" && !SameId(next.RequestedVehicleId, assignment.VehicleId))
                throw new InvalidOperationException("调度器分配的车辆与任务指定车辆不一致。");
            next.Status = RcsTaskStatus.Running; next.VehicleId = assignment.VehicleId; next.StartedAt = DateTime.UtcNow;
            if (!await store.TryStartAsync(next, stoppingToken)) return true;
            StartExecution(next, stoppingToken, "任务已分配给自动车辆");
            return true;
        }
        finally { _gate.Release(); }
    }

    private void StartExecution(RcsTaskRow task, CancellationToken lifetimeToken, string message)
    {
        var active = new ActiveTask(task, CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken));
        _active[task.VehicleId] = active;
        Publish(task, message);
        _executions[task.Id] = Task.Run(() => ExecuteTaskAsync(active, lifetimeToken));
    }

    // 所有执行任务均被观察；终态写库失败会传递给宿主，不能释放车辆继续下发。
    public async Task ObserveExecutionsAsync(bool drain = false)
    {
        var observed = _executions.Where(x => drain || x.Value.IsCompleted).ToArray();
        await Task.WhenAll(observed.Select(x => x.Value));
        foreach (var item in observed) _executions.TryRemove(item.Key, out _);
    }

    private async Task ExecuteTaskAsync(ActiveTask active, CancellationToken stoppingToken)
    {
        var task = active.Row;
        var cancellation = active.Cancellation;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
            { ["LogCategory"] = LogCategory.Task.ToString(), ["TaskId"] = task.TaskId });
        try
        {
            string status, message;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var stops = JsonSerializer.Deserialize<string[]>(task.StationCodesJson) ?? [];
                var plan = executor.Prepare(new(task.TaskId, task.VehicleId, task.MapCode, task.Warehouse,
                    stops, task.TaskType, task.ContainerCode,
                    JsonSerializer.Deserialize<string[]>(task.StationActionsJson) ?? []));
                Task execution;
                await _gate.WaitAsync(cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    task.MapVersion = plan.Map.Version; task.RouteJson = JsonSerializer.Serialize(plan.RoutePointCodes);
                    await store.UpdateAsync(task, cancellation.Token);
                    execution = executor.ExecuteAsync(plan, task.Status == RcsTaskStatus.Paused, cancellation.Token);
                    Publish(task, "任务路径已生成，虚拟车开始执行");
                }
                finally { _gate.Release(); }
                await execution; cancellation.Token.ThrowIfCancellationRequested();
                status = RcsTaskStatus.Completed; message = "路径和点位动作执行完成。";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                status = stoppingToken.IsCancellationRequested ? RcsTaskStatus.Interrupted : RcsTaskStatus.Cancelled;
                message = stoppingToken.IsCancellationRequested ? "RCS 停止，任务执行中断。" : "任务已取消。";
            }
            catch (Exception ex)
            {
                status = RcsTaskStatus.Failed; message = ex.Message;
                logger.LogError(ex, "任务执行失败 TaskId={TaskId} Vehicle={Vehicle}", task.TaskId, task.VehicleId);
            }
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (status == RcsTaskStatus.Completed && cancellation.IsCancellationRequested)
                {
                    status = stoppingToken.IsCancellationRequested ? RcsTaskStatus.Interrupted : RcsTaskStatus.Cancelled;
                    message = stoppingToken.IsCancellationRequested ? "RCS 停止，任务执行中断。" : "任务已取消。";
                }
                task.Status = status; task.Message = message; task.FinishedAt = DateTime.UtcNow;
                await store.UpdateAsync(task, CancellationToken.None);
                Publish(task, message); _active.TryRemove(task.VehicleId, out _);
            }
            finally { _gate.Release(); }
        }
        finally
        {
            executor.ClearVehiclePlanningGoal(task.VehicleId);
            cancellation.Dispose();
            NotifyWork();
        }
    }

    public async Task<bool> PauseAsync(string? taskId = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var active = FindActive(taskId);
            if (active is null || active.Row.Status is not (RcsTaskStatus.Running or RcsTaskStatus.Paused)) return false;
            var task = active.Row;
            var vehicle = executor.GetVehicles().FirstOrDefault(x => x.Id == task.VehicleId);
            if (vehicle?.TaskId == task.TaskId && vehicle.Status == "Arrived") return false;
            task.Status = RcsTaskStatus.Paused;
            await store.UpdateAsync(task, token); await executor.PauseAsync(task.VehicleId, token); Publish(task, "任务已暂停");
            return true;
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> ResumeAsync(string? taskId = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var active = FindActive(taskId);
            if (active is null || active.Row.Status != RcsTaskStatus.Paused) return false;
            var task = active.Row; task.Status = RcsTaskStatus.Running;
            await store.UpdateAsync(task, token); await executor.ResumeAsync(task.VehicleId, token); Publish(task, "任务继续执行");
            return true;
        }
        finally { _gate.Release(); }
    }
    public Task<bool> PauseVehicleAsync(string vehicleId, CancellationToken token = default) =>
        _active.TryGetValue(vehicleId, out var active) ? PauseAsync(active.Row.TaskId, token) : Task.FromResult(false);
    public Task<bool> ResumeVehicleAsync(string vehicleId, CancellationToken token = default) =>
        _active.TryGetValue(vehicleId, out var active) ? ResumeAsync(active.Row.TaskId, token) : Task.FromResult(false);
    public Task<bool> StopVehicleAsync(string vehicleId, CancellationToken token = default) =>
        _active.TryGetValue(vehicleId, out var active) ? CancelAsync(active.Row.TaskId, token) : Task.FromResult(false);

    public async Task<bool> CancelAsync(string taskId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var active = FindActive(taskId);
            if (active is not null)
            {
                active.Row.Status = RcsTaskStatus.Cancelling;
                await store.UpdateAsync(active.Row, token); active.Cancellation.Cancel();
                Publish(active.Row, "任务正在取消"); return true;
            }
            var waiting = (await store.FindAsync([taskId], token)).FirstOrDefault();
            if (waiting?.Status != RcsTaskStatus.Waiting) return false;
            waiting.Status = RcsTaskStatus.Cancelled; waiting.Message = "等待中的任务已取消。"; waiting.FinishedAt = DateTime.UtcNow;
            await store.UpdateAsync(waiting, token); Publish(waiting, waiting.Message); NotifyWork(); return true;
        }
        finally { _gate.Release(); }
    }

    public async Task ResetVehicleAsync(string pointCode, CancellationToken token = default, string vehicleId = "V-01",
        Func<Task>? savePosition = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            executor.ValidateReset(vehicleId, pointCode);
            if (_active.TryGetValue(vehicleId, out var active))
            {
                active.Row.Status = RcsTaskStatus.Cancelling;
                await store.UpdateAsync(active.Row, token); active.Cancellation.Cancel();
                Publish(active.Row, "车辆重置，当前任务正在取消");
            }
            await executor.ResetAsync(vehicleId, pointCode, token);
            if (savePosition is not null) await savePosition();
            NotifyWork();
        }
        finally { _gate.Release(); }
    }

    public async Task SetVehiclePositionAsync(string pointCode, CancellationToken token = default, string vehicleId = "V-01",
        Func<Task>? savePosition = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_active.ContainsKey(vehicleId))
                throw new RcsTaskConflictException($"车辆 {vehicleId} 有活动任务，不能修改当前位置。");
            executor.ValidateReset(vehicleId, pointCode);
            await executor.ResetAsync(vehicleId, pointCode, token);
            if (savePosition is not null) await savePosition();
            NotifyWork();
        }
        finally { _gate.Release(); }
    }

    public async Task<RcsTaskDto> ReplanAsync(string taskId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var active = FindActive(taskId);
            if (active is null || active.Row.Status is not (RcsTaskStatus.Running or RcsTaskStatus.Paused))
                throw new RcsTaskConflictException("任务不在执行或暂停状态。");
            var task = active.Row; var resume = task.Status == RcsTaskStatus.Running;
            task.Status = RcsTaskStatus.Paused;
            await store.UpdateAsync(task, token); await executor.PauseAsync(task.VehicleId, token);
            try
            {
                var plan = await executor.ReplanAsync(task.TaskId, token);
                task.RouteJson = JsonSerializer.Serialize(plan.RoutePointCodes); task.MapVersion = plan.Map.Version;
                await store.UpdateAsync(task, token);
                if (resume)
                {
                    task.Status = RcsTaskStatus.Running; await store.UpdateAsync(task, token);
                    await executor.ResumeAsync(task.VehicleId, token);
                }
                Publish(task, $"路径已更新为版本 {plan.RouteVersion}，保留已完成站点动作"); return task.ToDto();
            }
            catch (InvalidOperationException ex)
            {
                task.Status = RcsTaskStatus.Paused; await store.UpdateAsync(task, CancellationToken.None);
                Publish(task, $"重新规划未成功，车辆保持暂停：{ex.Message}"); throw new RcsTaskConflictException(ex.Message);
            }
        }
        finally { _gate.Release(); }
    }

    private ActiveTask? FindActive(string? taskId) => taskId is null ? _active.GetValueOrDefault("V-01")
        : _active.Values.FirstOrDefault(x => SameId(x.Row.TaskId, taskId));
    private void Publish(RcsTaskRow task, string message)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
            { ["LogCategory"] = LogCategory.Task.ToString(), ["TaskId"] = task.TaskId });
        logger.LogInformation("{Message} TaskId={TaskId} Status={Status} Vehicle={Vehicle} Priority={Priority}",
            message, task.TaskId, task.Status, task.VehicleId, task.PriorityCode);
        TaskChanged?.Invoke(task.ToDto());
    }
    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SameRequest(RcsTaskRow row, RcsTaskReceiveRequest group, RcsTaskRequest task, string source) =>
        row.Source == source && SameId(row.GroupId, group.GroupId) && SameId(row.Warehouse, group.Warehouse)
        && row.PriorityCode == group.PriorityCode && SameId(row.TaskType, task.TaskType)
        && SameId(row.RequestedVehicleId, task.VehicleId) && row.ContainerCode == task.ContainerCode
        && row.StationCodesJson == JsonSerializer.Serialize(task.StationCode)
        && row.StationActionsJson == JsonSerializer.Serialize(task.StationActions)
        && row.AreaCodesJson == JsonSerializer.Serialize(task.AreaCode);
    public static string ProtocolTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private sealed record ActiveTask(RcsTaskRow Row, CancellationTokenSource Cancellation);
}
