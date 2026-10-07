using Contracts.Rcs.Protocol;
using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Tasks;
using Contracts.Rcs.Vehicle;
using Rcs.Algorithms;
using Rcs.Algorithms.Traffic;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;
using RCSBackend.Modules.Rcs.Application.Maps;
using RCSBackend.Modules.Rcs.Application.Inventory;
using RCSBackend.Modules.Rcs.Protocol;
using Contracts.Rcs.StationBusiness;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>编排叫车、起点动作、运送和终点动作；路径窗口与锁由 RcsRouteWindowManager 协调。</summary>
public sealed class RcsTaskExecutionService : IRcsTaskExecutionService, IDisposable
{
    private readonly RcsMapCache _maps;
    private readonly RcsTaskRoutePlanner _planner;
    private readonly RcsAlgorithmSettingsService _algorithmSettings;
    private readonly IMultiVehicleTrafficCoordinator _traffic;
    private readonly RcsVehicleTrafficStateSynchronizer _trafficState;
    private readonly IVehicleCommandChannel _channel;
    private readonly RcsInventoryTransferService _inventory;
    private readonly RcsTaskStationRuleCoordinator _stationRuleCoordinator;
    private readonly RcsVehicleActionExecutor _vehicleActionExecutor;
    private readonly RcsRouteWindowManager _routeWindows;
    private readonly ILogger<RcsTaskExecutionService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, RcsTaskExecutionSession> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastObservedPoints = new(StringComparer.OrdinalIgnoreCase);
    private event Action<VehicleStateDto>? _vehicleStateChanged;
    private event Action? _inventoryChanged;

    public RcsTaskExecutionService(RcsMapCache maps, RcsTaskRoutePlanner planner,
        RcsAlgorithmSettingsService algorithmSettings, IMultiVehicleTrafficCoordinator traffic,
        RcsVehicleTrafficStateSynchronizer trafficState, IVehicleCommandChannel channel,
        RcsInventoryTransferService inventory, RcsTaskStationRuleCoordinator stationRuleCoordinator,
        RcsVehicleActionExecutor vehicleActionExecutor, RcsRouteWindowManager routeWindows,
        ILogger<RcsTaskExecutionService> logger)
    {
        _maps = maps; _planner = planner; _algorithmSettings = algorithmSettings;
        _traffic = traffic; _trafficState = trafficState; _channel = channel;
        _inventory = inventory; _stationRuleCoordinator = stationRuleCoordinator;
        _vehicleActionExecutor = vehicleActionExecutor; _routeWindows = routeWindows; _logger = logger;
        _channel.StateChanged += OnVehicleStateChanged;
        _inventory.InventoryChanged += OnInventoryChanged;
    }

    public event Action<VehicleStateDto>? VehicleStateChanged
    {
        add => _vehicleStateChanged += value;
        remove => _vehicleStateChanged -= value;
    }

    public event Action? InventoryChanged
    {
        add => _inventoryChanged += value;
        remove => _inventoryChanged -= value;
    }

    public IReadOnlyList<VehicleStateDto> GetVehicles()
    {
        var states = _channel.GetStates();
        _trafficState.SyncFleet(states);
        return states.Select(_trafficState.EnrichState).ToArray();
    }

    public RcsTaskExecutionPlan Prepare(RcsTaskExecutionRequest request)
    {
        var states = _channel.GetStates();
        _trafficState.SyncFleet(states);
        var state = states.FirstOrDefault(x => string.Equals(x.Id, request.VehicleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"车辆 {request.VehicleId} 不存在。");
        var map = _maps.Current ?? throw new InvalidOperationException("当前地图未加载。");
        EnsureMap(map.MapCode, map.SceneName, request.MapCode, request.Warehouse);
        var stops = RcsTaskRoutePlanner.CreateStops(request).Select(stop =>
        {
            if (!map.TryGetPoint(stop.PointCode, out var node))
                throw new ArgumentException($"任务站点 {stop.PointCode} 不存在或楼层与地图 Z 不匹配。");
            return stop with { PointCode = node.PointCode };
        }).ToArray();
        try
        {
            var fetchIndex = stops.ToList().FindIndex(x => x.Action == VehiclePointAction.Fetch);
            var putIndex = stops.ToList().FindIndex(x => x.Action == VehiclePointAction.Put);
            var hasInventoryActions = fetchIndex >= 0 || putIndex >= 0;
            var syncInventory = fetchIndex >= 0 && putIndex > fetchIndex
                && stops.Count(x => x.Action == VehiclePointAction.Fetch) == 1
                && stops.Count(x => x.Action == VehiclePointAction.Put) == 1;
            if (hasInventoryActions && (!syncInventory || string.IsNullOrWhiteSpace(request.ContainerCode)))
                throw new ArgumentException("库存搬运任务必须先取货再放货，且只能各执行一次，并指定货物或托盘编码。");
            IReadOnlyList<RcsTaskStop> activeStops;
            IReadOnlyList<RcsTaskStop> deferredStops;
            if (syncInventory)
            {
                activeStops = stops.Take(fetchIndex + 1).ToArray();
                deferredStops = stops.Skip(fetchIndex + 1).ToArray();
            }
            else if (stops.Length == 1)
            {
                // A one-station move task has no explicit origin. Use the vehicle's reported
                // current point as the start phase, then defer the requested destination.
                activeStops = [new RcsTaskStop($"{request.TaskId}:start", state.PointCode, VehiclePointAction.Move)];
                deferredStops = stops;
            }
            else
            {
                activeStops = stops.Take(1).ToArray();
                deferredStops = stops.Skip(1).ToArray();
            }
            // Only the active leg is given to traffic coordination. The next leg is planned after
            // the vehicle reports arrival and confirms the start-point action.
            _traffic.SetVehicleGoals(state.Id, activeStops.Select(x => x.PointCode).ToArray());
            return new(request.TaskId, request.VehicleId, map,
                _planner.Plan(map, _traffic.GetPlanningContext(state.Id), state.PointCode, activeStops,
                    _algorithmSettings.Current), activeStops, request.ContainerCode, SyncInventory: syncInventory,
                DeferredStops: deferredStops, AllStops: stops, Warehouse: request.Warehouse);
        }
        catch
        {
            _traffic.SetVehicleGoals(state.Id, []);
            throw;
        }
    }

    public void ClearVehiclePlanningGoal(string vehicleId) => _trafficState.ClearGoal(vehicleId);

    private void PlanNextLeg(RcsTaskExecutionSession execution, IReadOnlyList<RcsTaskStop> stops)
    {
        var state = Vehicle(execution.Plan.VehicleId);
        var currentMap = _maps.Current;
        EnsureMap(execution.Plan.Map.MapCode, execution.Plan.Map.SceneName,
            currentMap?.MapCode ?? "", currentMap?.SceneName ?? "");
        _traffic.SetVehicleGoals(execution.Plan.VehicleId, stops.Select(stop => stop.PointCode).ToArray());
        var route = _planner.Plan(execution.Plan.Map, _traffic.GetPlanningContext(execution.Plan.VehicleId),
            state.PointCode, stops, _algorithmSettings.Current, retainAnchor: true);
        var nextVersion = checked(execution.Plan.RouteVersion + 1);
        execution.Plan = execution.Plan with
        {
            RoutePlan = route,
            Stops = stops,
            DeferredStops = [],
            RouteVersion = nextVersion
        };
        execution.AuthorizedEntryIndices.Clear();
        execution.CheckedEntryIndices.Clear();
    }

    private string ConfirmVehicleAtPoint(RcsTaskExecutionSession execution, RcsTaskStop stop)
    {
        var state = Vehicle(execution.Plan.VehicleId);
        if (state.TaskId != execution.Plan.TaskId || state.PointCode != stop.PointCode
            || state.Status is "Running" or "Paused" || state.LastAction != VehiclePointAction.Move)
            throw new InvalidOperationException($"车辆未确认在 {stop.PointCode} 完成 move 动作。");
        return execution.CommandId;
    }

    private async Task<string?> ExecutePointActionAsync(RcsTaskExecutionSession execution, RcsTaskStop stop, CancellationToken token)
    {
        await _stationRuleCoordinator.RunAsync(execution.Plan, stop.PointCode, RcsStationEvents.BeforeAction, stop.Action, token);
        var command = stop.Action is VehiclePointAction.Fetch or VehiclePointAction.Put
            ? await ExecuteStationActionAsync(execution, stop, token)
            : ConfirmVehicleAtPoint(execution, stop);
        await _stationRuleCoordinator.RunAsync(execution.Plan, stop.PointCode, RcsStationEvents.AfterAction, stop.Action, token);
        return command;
    }

    private async Task ReserveInventoryAsync(RcsTaskExecutionPlan plan, CancellationToken token)
    {
        if (!plan.SyncInventory) return;
        var fetch = plan.TaskStops.SingleOrDefault(x => x.Action == VehiclePointAction.Fetch);
        var put = plan.TaskStops.SingleOrDefault(x => x.Action == VehiclePointAction.Put);
        if (fetch is null || put is null || fetch.PointCode == put.PointCode || string.IsNullOrWhiteSpace(plan.ContainerCode))
            throw new InvalidOperationException("库存搬运任务必须包含不同起终点的一次取货和一次放货，并指定货物或托盘编码。");
        await _inventory.ReserveAsync(new RcsInventoryMove(plan.TaskId, plan.Map.MapCode,
            plan.ContainerCode, fetch.PointCode, put.PointCode), token);
    }

    public async Task ExecuteAsync(RcsTaskExecutionPlan plan, bool startPaused,
        Func<RcsTaskExecutionStageDto, Task>? stageChanged, CancellationToken token)
    {
        Vehicle(plan.VehicleId);
        token.ThrowIfCancellationRequested();
        var execution = new RcsTaskExecutionSession(plan, Guid.NewGuid().ToString("N"), token);
        lock (_gate)
        {
            if (!_current.TryAdd(plan.VehicleId, execution)) throw new InvalidOperationException("该车辆已有执行任务。");
        }
        try
        {
            await ReserveInventoryAsync(plan, token);
            var startStop = plan.Stops.LastOrDefault()
                ?? throw new InvalidOperationException("任务没有可执行的起点阶段。");
            var endStops = plan.RemainingStageStops;
            var endStop = endStops.LastOrDefault() ?? plan.TaskStops.LastOrDefault()
                ?? throw new InvalidOperationException("任务没有可执行的终点阶段。");
            var startFrom = Vehicle(plan.VehicleId).PointCode;
            await RunReportedStageAsync(stageChanged,
                Stage(1, "VEHICLE_TO_START", "车辆从当前位置前往起点", startFrom, startStop.PointCode,
                    "", plan.VehicleId, plan.RoutePointCodes),
                async () =>
                {
                    await _routeWindows.ExecuteLegAsync(execution, startPaused, PublishCurrentState, token);
                    return ConfirmVehicleAtPoint(execution, startStop);
                });

            await RunReportedStageAsync(stageChanged,
                Stage(2, "START_ACTION", "车辆在起点执行起点动作", startStop.PointCode, startStop.PointCode,
                    startStop.Action, plan.VehicleId),
                () => ExecutePointActionAsync(execution, startStop, token));

            token.ThrowIfCancellationRequested();
            var stateAtStart = Vehicle(plan.VehicleId);
            if (stateAtStart.Status != "Arrived" || stateAtStart.TaskId != plan.TaskId
                || stateAtStart.PointCode != startStop.PointCode
                || startStop.Action is VehiclePointAction.Fetch or VehiclePointAction.Put
                    && !stateAtStart.CompletedStepIds.Contains(startStop.StepId, StringComparer.Ordinal))
                throw new InvalidOperationException("车辆尚未确认起点动作完成，不能继续规划终点路径。");

            _logger.LogInformation("开始计算起点动作确认后的目标路径 TaskId={TaskId} Vehicle={Vehicle} From={Point} To={Destination}",
                plan.TaskId, plan.VehicleId, stateAtStart.PointCode, endStop.PointCode);
            PlanNextLeg(execution, endStops);

            await RunReportedStageAsync(stageChanged,
                Stage(3, "VEHICLE_TO_END", "车辆从起点前往终点", stateAtStart.PointCode,
                    endStop.PointCode, "", plan.VehicleId, execution.Plan.RoutePointCodes),
                async () =>
                {
                    await _routeWindows.ExecuteLegAsync(execution, false, PublishCurrentState, token);
                    return ConfirmVehicleAtPoint(execution, endStop);
                });
            await RunReportedStageAsync(stageChanged,
                Stage(4, "END_ACTION", "车辆在终点执行终点动作", endStop.PointCode, endStop.PointCode,
                    endStop.Action, plan.VehicleId),
                () => ExecutePointActionAsync(execution, endStop, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 取消等待不代表车已停。发送 STOP 并确认完成后，任务服务才能释放车辆。
            var state = Vehicle(plan.VehicleId);
            if (execution.HasAcceptedCommand && state.TaskId == plan.TaskId && state.Status is "Running" or "Paused")
            {
                await execution.WindowGate.WaitAsync(CancellationToken.None);
                try
                {
                    await SendCheckedAsync(Control(plan.VehicleId, plan.TaskId, VehicleCommandType.Stop), CancellationToken.None);
                    await _channel.WaitForCompletionAsync(plan.VehicleId, execution.CommandId, CancellationToken.None);
                }
                finally { execution.WindowGate.Release(); }
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (_current.TryGetValue(plan.VehicleId, out var current) && ReferenceEquals(current, execution))
                    _current.Remove(plan.VehicleId);
            }
            _traffic.SetVehicleGoals(plan.VehicleId, []);
            _inventory.Release(plan.TaskId);
        }
    }

    public async Task PauseAsync(string vehicleId, CancellationToken token = default)
    {
        var execution = FindExecution(vehicleId);
        if (execution is not null) await execution.WindowGate.WaitAsync(token);
        try
        {
            var state = Vehicle(vehicleId);
            if (state.Status is "Running" or "Paused")
                await SendCheckedAsync(Control(vehicleId, state.TaskId, VehicleCommandType.Pause), token);
        }
        finally { if (execution is not null) execution.WindowGate.Release(); }
    }

    public async Task ResumeAsync(string vehicleId, CancellationToken token = default)
    {
        var execution = FindExecution(vehicleId);
        if (execution is not null) await execution.WindowGate.WaitAsync(token);
        try
        {
            var state = Vehicle(vehicleId);
            if (state.Status == "Paused") await SendCheckedAsync(Control(vehicleId, state.TaskId, VehicleCommandType.Resume), token);
        }
        finally { if (execution is not null) execution.WindowGate.Release(); }
    }

    /// <summary>任务服务先暂停并持久化，再改路；只发送新路线的首个窗口。</summary>
    public Task<RcsTaskExecutionPlan> ReplanAsync(string taskId, CancellationToken token = default)
    {
        RcsTaskExecutionSession execution;
        lock (_gate) execution = _current.Values.FirstOrDefault(x => x.Plan.TaskId == taskId) is { } current ? current
            : throw new InvalidOperationException("任务尚未开始车辆执行或已经结束。");
        return _routeWindows.ReplanAsync(execution, taskId, PublishCurrentState, token);
    }

    public void ValidateReset(string vehicleId, string pointCode)
    {
        Vehicle(vehicleId);
        if (!string.IsNullOrEmpty(pointCode) && _maps.Current?.TryGetPoint(pointCode, out _) != true)
            throw new ArgumentException("重置站点不在当前可用地图中。");
    }

    public async Task ResetAsync(string vehicleId, string pointCode, CancellationToken token = default)
    {
        ValidateReset(vehicleId, pointCode);
        var execution = FindExecution(vehicleId);
        if (execution is not null) await execution.WindowGate.WaitAsync(token);
        try
        {
            var point = string.IsNullOrEmpty(pointCode) ? null
                : _maps.Current!.TryGetPoint(pointCode, out var node) ? RcsTaskRoutePlanner.ToPoint(node)
                : throw new ArgumentException("重置站点不在当前可用地图中。");
            var lockedPoints = point is null ? Array.Empty<string>() : [point.PointCode];
            using var lockLease = await _traffic.AcquirePositionAsync(vehicleId, Vehicle(vehicleId).TaskId,
                lockedPoints.FirstOrDefault() ?? "", token);
            await SendCheckedAsync(Control(vehicleId, Vehicle(vehicleId).TaskId, VehicleCommandType.Reset) with { ResetPoint = point }, token);
            lockLease.Commit();
            PublishCurrentState(vehicleId);
        }
        finally { if (execution is not null) execution.WindowGate.Release(); }
    }

    private void OnVehicleStateChanged(VehicleStateDto state)
    {
        string? previousPoint;
        lock (_gate) { _lastObservedPoints.TryGetValue(state.Id, out previousPoint); if (!string.IsNullOrWhiteSpace(state.PointCode)) _lastObservedPoints[state.Id] = state.PointCode; }
        if (!string.IsNullOrWhiteSpace(previousPoint) && !string.Equals(previousPoint, state.PointCode, StringComparison.OrdinalIgnoreCase))
        {
            RcsTaskExecutionSession? observed; lock (_gate) _current.TryGetValue(state.Id, out observed);
            if (observed is not null && observed.Plan.TaskId == state.TaskId)
            {
                _ = _stationRuleCoordinator.RunSafelyAsync(observed.Plan, previousPoint, RcsStationEvents.AfterLeave, "", CancellationToken.None);
                _ = _stationRuleCoordinator.RunSafelyAsync(observed.Plan, state.PointCode, RcsStationEvents.AfterEnter, "", CancellationToken.None);
            }
        }
        _trafficState.ObservePosition(state);
        if (state.Status is "Running" or "Paused")
            _traffic.ObserveProgress(state.Id, state.TaskId, state.RouteVersion, state.RouteIndex, state.PointCode);
        else _trafficState.ObserveStationaryPosition(state);
        _vehicleStateChanged?.Invoke(_trafficState.EnrichState(state));
        if (state.Status != "Running") return;
        RcsTaskExecutionSession? execution;
        lock (_gate) _current.TryGetValue(state.Id, out execution);
        if (execution is not null && execution.Plan.TaskId == state.TaskId)
            _ = _routeWindows.AdvanceAsync(execution, PublishCurrentState);
    }

    private async Task<string?> ExecuteStationActionAsync(RcsTaskExecutionSession execution, RcsTaskStop stop, CancellationToken token)
    {
        var commandId = Guid.NewGuid().ToString("N");
        execution.CommandId = commandId;
        execution.RouteUpdateError = null;
        await execution.WindowGate.WaitAsync(token);
        try
        {
            var state = Vehicle(execution.Plan.VehicleId);
            if (state.Status != "Arrived" || state.TaskId != execution.Plan.TaskId || state.PointCode != stop.PointCode)
                throw new InvalidOperationException($"车辆未确认到达动作站点 {stop.PointCode}。");
            await _vehicleActionExecutor.SendAsync(new VehicleCommand
            {
                CommandId = commandId, VehicleId = execution.Plan.VehicleId, TaskId = execution.Plan.TaskId,
                Type = VehicleCommandType.ExecuteAction, RouteVersion = execution.RouteVersion,
                Action = stop.Action, ActionStepId = stop.StepId, ExpectedPointCode = stop.PointCode,
                ContainerCode = execution.Plan.ContainerCode
            }, () => execution.HasAcceptedCommand = true, () => PublishCurrentState(execution.Plan.VehicleId), token);
        }
        finally { execution.WindowGate.Release(); }

        return await _vehicleActionExecutor.WaitForCompletionAndApplyInventoryAsync(execution.Plan, stop, commandId,
            () => Vehicle(execution.Plan.VehicleId), () => PublishCurrentState(execution.Plan.VehicleId), token);
    }

    private static RcsTaskExecutionStageDto Stage(int sequence, string code, string name, string from,
        string to, string action, string vehicleId, IReadOnlyList<string>? routePointCodes = null) => new()
    {
        Sequence = sequence, StageCode = code, Name = name,
        FromPointCode = from, ToPointCode = to, Action = action, VehicleId = vehicleId,
        RoutePointCodes = routePointCodes ?? []
    };

    private static async Task RunReportedStageAsync(Func<RcsTaskExecutionStageDto, Task>? stageChanged,
        RcsTaskExecutionStageDto stage, Func<Task<string?>> execute)
    {
        var startedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (stageChanged is not null)
            await stageChanged(stage with { Status = RcsTaskExecutionStageStatus.Running, StartedAt = startedAt, Message = "执行中" });
        try
        {
            var commandId = await execute();
            if (stageChanged is not null)
                await stageChanged(stage with
                {
                    Status = RcsTaskExecutionStageStatus.Completed, CommandId = commandId ?? "",
                    StartedAt = startedAt,
                    FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    Message = "车辆已确认完成"
                });
        }
        catch (Exception ex)
        {
            if (stageChanged is not null)
                await stageChanged(stage with
                {
                    Status = ex is OperationCanceledException ? RcsTaskExecutionStageStatus.Cancelled : RcsTaskExecutionStageStatus.Failed,
                    StartedAt = startedAt,
                    FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    Message = ex.Message
                });
            throw;
        }
    }

    private void OnInventoryChanged() => _inventoryChanged?.Invoke();

    private VehicleStateDto Vehicle(string id) => _channel.GetStates().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"车辆 {id} 不存在。");

    private void PublishCurrentState(string vehicleId)
    {
        var state = _channel.GetStates().FirstOrDefault(x => string.Equals(x.Id, vehicleId, StringComparison.OrdinalIgnoreCase));
        if (state is not null) _vehicleStateChanged?.Invoke(_trafficState.EnrichState(state));
    }

    private RcsTaskExecutionSession? FindExecution(string vehicleId)
    {
        lock (_gate) return _current.GetValueOrDefault(vehicleId);
    }

    private async Task SendCheckedAsync(VehicleCommand command, CancellationToken token)
    {
        var ack = await _channel.SendAsync(command, token);
        if (!ack.Accepted) throw new InvalidOperationException(ack.Message);
    }

    private static VehicleCommand Control(string vehicleId, string taskId, string type) => new()
        { CommandId = Guid.NewGuid().ToString("N"), VehicleId = vehicleId, TaskId = taskId, Type = type };

    private static void EnsureMap(string actualCode, string actualScene, string expectedCode, string expectedScene)
    {
        if (actualCode != expectedCode || !string.Equals(actualScene, expectedScene, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前地图已切换，任务需要的地图或场景不再可用。");
    }

    public void Dispose()
    {
        _channel.StateChanged -= OnVehicleStateChanged;
        _inventory.InventoryChanged -= OnInventoryChanged;
    }
}
