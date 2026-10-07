using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Backend.Shared.Logging;
using Contracts.Rcs.Tasks;
using RCSBackend.Modules.Rcs.Application.Execution;
using RCSBackend.Modules.Rcs.Application.Inventory;
using RCSBackend.Modules.Rcs.Application.Maps;
using RCSBackend.Modules.Rcs.Application.Scheduling;
using RCSBackend.Modules.Rcs.Application.Vehicles;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

/// <summary>任务调度与每车生命周期；接收和只读查询由独立服务负责。长时间执行不占用调度锁。</summary>
public sealed class RcsTaskService(IRcsTaskStore store, RcsMapCache maps, IRcsTaskScheduler scheduler,
    IRcsTaskExecutionService executor, RcsTaskAcceptanceService acceptance, RcsDispatchLock dispatchLock,
    RcsInventoryTransferService inventory, ILogger<RcsTaskService> logger) : IRcsTaskService, IRcsTaskDispatchRuntime
{
    private readonly SemaphoreSlim _gate = dispatchLock.Gate;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<string, ActiveTask> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Task> _executions = new();
    private CancellationToken _executionLifetime;
    private bool _clearing;
    public event Action<RcsTaskDto>? TaskChanged;
    public event Action<int>? TasksCleared;
    public void NotifyWork() => _wake.Writer.TryWrite(true);
    public void SetExecutionLifetime(CancellationToken token) => _executionLifetime = token;
    public bool IsVehicleBusy(string vehicleId) => _active.ContainsKey(vehicleId);

    public Task<RcsApiResponse> ReceiveAsync(RcsTaskReceiveRequest request, string originalRequestJson, CancellationToken token = default) =>
        ReceiveFromAsync(request, RcsTaskSource.Upstream, originalRequestJson, token);

    public Task<RcsApiResponse> ReceiveManualAsync(RcsTaskReceiveRequest request, CancellationToken token = default) =>
        ReceiveFromAsync(request, RcsTaskSource.Manual, "", token);

    private async Task<RcsApiResponse> ReceiveFromAsync(RcsTaskReceiveRequest request, string source, string originalRequestJson, CancellationToken token)
    {
        await acceptance.ValidateAndInitializeAsync(request, source, token);
        await _gate.WaitAsync(token);
        try
        {
            if (_clearing) throw new RcsTaskConflictException("正在清空任务，请稍后重试。");
            var added = await acceptance.AcceptAsync(request, source, originalRequestJson,
                vehicleId => _active.ContainsKey(vehicleId), Publish, token);
            foreach (var task in added)
            {
                if (source == RcsTaskSource.Manual) StartExecution(task, _executionLifetime, "手动任务已直接下发给指定车辆");
                else Publish(task, "任务已接收并进入等待队列");
            }
            if (source == RcsTaskSource.Upstream) NotifyWork();
            return RcsApiResponse.Accepted();
        }
        finally { _gate.Release(); }
    }

    public async Task<int> ClearAllAsync(CancellationToken token = default)
    {
        ActiveTask[] active;
        var clearStarted = false;
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                if (_clearing) throw new RcsTaskConflictException("任务清空操作已在进行中。");
                _clearing = true;
                clearStarted = true;
                active = _active.Values.ToArray();
                foreach (var task in active)
                {
                    task.Row.Status = RcsTaskStatus.Cancelling;
                    task.Row.Message = "用户正在清空任务，等待车辆停止确认。";
                    await store.UpdateAsync(task.Row, token);
                    task.Cancellation.Cancel();
                    Publish(task.Row, "清空任务：正在请求车辆停止");
                }
            }
            finally { _gate.Release(); }

            var executions = active.Select(task => _executions.TryGetValue(task.Row.Id, out var execution)
                ? execution : Task.CompletedTask).ToArray();
            try { await Task.WhenAll(executions).WaitAsync(TimeSpan.FromSeconds(45), token); }
            catch (TimeoutException) { throw new RcsTaskConflictException("等待活动车辆停止超时，任务记录已保留；请确认车辆状态后再清空。"); }

            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (!_active.IsEmpty) throw new RcsTaskConflictException("仍有车辆任务未完成停止确认，任务记录已保留。");
                if (active.Any(task => task.Row.Status is not (RcsTaskStatus.Cancelled or RcsTaskStatus.Completed)))
                    throw new RcsTaskConflictException("至少一台车辆未能确认安全停止，任务记录已保留；请检查车辆状态后再清空。");
                var waitingReservations = await store.WaitingCandidatesAsync(RcsTaskSource.Upstream, token);
                var deletedCount = await store.DeleteAllAsync(token);
                foreach (var waiting in waitingReservations) inventory.Release(waiting.TaskId);
                TasksCleared?.Invoke(deletedCount);
                return deletedCount;
            }
            finally
            {
                _clearing = false;
                _gate.Release();
            }
        }
        catch
        {
            if (clearStarted) await ResetClearingAsync();
            throw;
        }
    }

    private async Task ResetClearingAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try { _clearing = false; }
        finally { _gate.Release(); }
    }
    public async Task RecoverAsync(CancellationToken token)
    {
        await store.RecoverAsync(token);
        var map = maps.Current;
        if (map is null) return;

        foreach (var task in await store.WaitingTasksAsync(RcsTaskSource.Upstream, token))
        {
            try
            {
                if (!SameId(task.MapCode, map.MapCode) || !SameId(task.Warehouse, map.SceneName))
                    throw new ArgumentException("等待任务使用的地图或场景与当前地图不一致。");
                var codes = JsonSerializer.Deserialize<string[]>(task.StationCodesJson) ?? [];
                var normalized = codes.Select(code => map.TryGetPoint(code, out var point)
                    ? point.PointCode
                    : throw new ArgumentException($"等待任务站点 {code} 不存在或楼层与当前地图不匹配。")).ToArray();
                RcsTaskRouteValidator.Validate(map, task.TaskId, normalized);
                var actions = JsonSerializer.Deserialize<string[]>(task.StationActionsJson) ?? [];
                var stops = RcsTaskRoutePlanner.CreateStops(new(task.TaskId, task.RequestedVehicleId, map.MapCode,
                    task.Warehouse, normalized, task.TaskType, task.ContainerCode, actions));
                var fetch = stops.Where(x => x.Action == Contracts.Rcs.Protocol.VehiclePointAction.Fetch).ToArray();
                var put = stops.Where(x => x.Action == Contracts.Rcs.Protocol.VehiclePointAction.Put).ToArray();
                if (fetch.Length == 0 && put.Length == 0) continue;
                var stopArray = stops.ToArray();
                if (fetch.Length != 1 || put.Length != 1
                    || Array.IndexOf(stopArray, fetch[0]) >= Array.IndexOf(stopArray, put[0]))
                    throw new ArgumentException("等待中的库存搬运任务必须先执行一次 fetch，再执行一次 put。");
                await inventory.ReserveBeforeAcceptanceAsync(task.TaskId, map.MapCode, task.ContainerCode,
                    fetch[0].PointCode, put[0].PointCode, token);
            }
            catch (Exception ex) when (ex is ArgumentException or RcsInventoryPreconditionException or JsonException)
            {
                task.Status = RcsTaskStatus.Failed;
                task.Message = ex.Message;
                task.FinishedAt = DateTime.UtcNow;
                await store.UpdateAsync(task, token);
                Publish(task, ex.Message);
            }
        }
    }
    public async Task WaitForWorkAsync(CancellationToken token) => await _wake.Reader.ReadAsync(token);

    // 只领取并启动一个任务，立即返回，后台循环可继续给其他空闲车分配。
    public async Task<bool> DispatchNextAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken);
        try
        {
            if (_clearing) return false;
            var waiting = await store.WaitingCandidatesAsync(RcsTaskSource.Upstream, stoppingToken);
            var available = executor.GetVehicles().Where(x => !_active.ContainsKey(x.Id)).ToArray();
            var assignment = scheduler.Select(waiting, available);
            if (assignment is null) return false;
            if (_active.ContainsKey(assignment.VehicleId) || !available.Any(x => x.Id == assignment.VehicleId && x.IsEnabled && x.IsOnline
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
                    task.ExecutionStagesJson = JsonSerializer.Serialize(RcsTaskExecutionStageFactory.CreateForPlan(plan));
                    await store.UpdateAsync(task, cancellation.Token);
                    execution = executor.ExecuteAsync(plan, task.Status == RcsTaskStatus.Paused,
                        stage => UpdateExecutionStageAsync(task, stage), cancellation.Token);
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
            inventory.Release(task.TaskId);
            cancellation.Dispose();
            NotifyWork();
        }
    }

    public async Task<bool> PauseAsync(string? taskId = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_clearing) throw new RcsTaskConflictException("正在清空任务，暂不能修改车辆任务状态。");
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
            if (_clearing) throw new RcsTaskConflictException("正在清空任务，暂不能修改车辆任务状态。");
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
            if (_clearing) throw new RcsTaskConflictException("正在清空任务，请稍后重试。");
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
            await store.UpdateAsync(waiting, token); inventory.Release(waiting.TaskId);
            Publish(waiting, waiting.Message); NotifyWork(); return true;
        }
        finally { _gate.Release(); }
    }

    public async Task ResetVehicleAsync(string pointCode, CancellationToken token = default, string vehicleId = "V-01",
        Func<Task>? savePosition = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_clearing) throw new RcsTaskConflictException("正在清空任务，请稍后重试。");
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
            if (_clearing) throw new RcsTaskConflictException("正在清空任务，请稍后重试。");
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
            if (_clearing) throw new RcsTaskConflictException("正在清空任务，请稍后重试。");
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

    private async Task UpdateExecutionStageAsync(RcsTaskRow task, RcsTaskExecutionStageDto stage)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            var stages = JsonSerializer.Deserialize<List<RcsTaskExecutionStageDto>>(task.ExecutionStagesJson) ?? [];
            var persistedStage = stage with { RoutePointCodes = [] };
            var index = stages.FindIndex(x => x.StageCode == stage.StageCode);
            if (index < 0) stages.Add(persistedStage); else stages[index] = persistedStage;
            task.ExecutionStagesJson = JsonSerializer.Serialize(stages.OrderBy(x => x.Sequence));
            if (stage.Status == RcsTaskExecutionStageStatus.Running && stage.RoutePointCodes.Count > 0)
            {
                if (stage.Sequence == 1)
                    task.RouteJson = JsonSerializer.Serialize(stage.RoutePointCodes);
                else if (stage.Sequence == 3)
                {
                    var firstLeg = JsonSerializer.Deserialize<string[]>(task.RouteJson) ?? [];
                    var fullRoute = firstLeg.ToList();
                    var endOffset = firstLeg.Length > 0
                        && string.Equals(firstLeg[^1], stage.RoutePointCodes[0], StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    fullRoute.AddRange(stage.RoutePointCodes.Skip(endOffset));
                    task.RouteJson = JsonSerializer.Serialize(fullRoute);
                }
            }
            task.UpdatedAt = DateTime.UtcNow;
            await store.UpdateAsync(task, CancellationToken.None);
            Publish(task, $"RCS 执行阶段：{stage.Name} {stage.Status}");
        }
        finally { _gate.Release(); }
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static string ProtocolTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private sealed record ActiveTask(RcsTaskRow Row, CancellationTokenSource Cancellation);
}
