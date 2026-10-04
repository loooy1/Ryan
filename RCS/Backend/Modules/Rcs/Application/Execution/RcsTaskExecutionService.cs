using Contracts.Rcs.Protocol;
using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Vehicle;
using Rcs.Algorithms;
using Rcs.Algorithms.Traffic;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;
using RCSBackend.Modules.Rcs.Protocol;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>负责路径规划及协议执行；不选择任务、不更新任务表。</summary>
public sealed class RcsTaskExecutionService : IRcsTaskExecutionService, IDisposable
{
    private readonly RcsMapCache _maps;
    private readonly RcsTaskRoutePlanner _planner;
    private readonly RcsAlgorithmSettingsService _algorithmSettings;
    private readonly IMultiVehicleTrafficCoordinator _traffic;
    private readonly IVehicleCommandChannel _channel;
    private readonly ILogger<RcsTaskExecutionService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Execution> _current = new(StringComparer.OrdinalIgnoreCase);
    private event Action<VehicleStateDto>? _vehicleStateChanged;

    public RcsTaskExecutionService(RcsMapCache maps, RcsTaskRoutePlanner planner,
        RcsAlgorithmSettingsService algorithmSettings, IMultiVehicleTrafficCoordinator traffic,
        IVehicleCommandChannel channel, ILogger<RcsTaskExecutionService> logger)
    {
        _maps = maps; _planner = planner; _algorithmSettings = algorithmSettings;
        _traffic = traffic; _channel = channel; _logger = logger;
        _channel.StateChanged += OnVehicleStateChanged;
    }

    public event Action<VehicleStateDto>? VehicleStateChanged
    {
        add => _vehicleStateChanged += value;
        remove => _vehicleStateChanged -= value;
    }

    public IReadOnlyList<VehicleStateDto> GetVehicles()
    {
        var states = _channel.GetStates();
        SyncFleet(states);
        foreach (var state in states.Where(x => x.Status is not ("Running" or "Paused")))
            ObserveStationaryPosition(state);
        return states.Select(EnrichState).ToArray();
    }

    public RcsTaskExecutionPlan Prepare(RcsTaskExecutionRequest request)
    {
        var states = _channel.GetStates();
        SyncFleet(states);
        var state = states.FirstOrDefault(x => string.Equals(x.Id, request.VehicleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"车辆 {request.VehicleId} 不存在。");
        var map = _maps.Current ?? throw new InvalidOperationException("当前地图未加载。");
        EnsureMap(map.MapCode, map.SceneName, request.MapCode, request.Warehouse);
        var stops = RcsTaskRoutePlanner.CreateStops(request);
        _traffic.SetVehicleGoals(state.Id, stops.Select(x => x.PointCode).ToArray());
        try
        {
            return new(request.TaskId, request.VehicleId, map,
                _planner.Plan(map, _traffic.GetPlanningContext(state.Id), state.PointCode, stops,
                    _algorithmSettings.Current), stops, request.ContainerCode);
        }
        catch
        {
            _traffic.SetVehicleGoals(state.Id, []);
            throw;
        }
    }

    public void ClearVehiclePlanningGoal(string vehicleId) => _traffic.SetVehicleGoals(vehicleId, []);

    public async Task ExecuteAsync(RcsTaskExecutionPlan plan, bool startPaused, CancellationToken token)
    {
        Vehicle(plan.VehicleId);
        token.ThrowIfCancellationRequested();
        var execution = new Execution(plan, Guid.NewGuid().ToString("N"), token);
        var accepted = false;
        lock (_gate)
        {
            if (!_current.TryAdd(plan.VehicleId, execution)) throw new InvalidOperationException("该车辆已有执行任务。");
        }
        try
        {
            await execution.WindowGate.WaitAsync(token);
            try
            {
                var firstSegment = GetFirstSegment(execution.Plan.RoutePlan);
                var lockLease = await AcquireFirstWindowAsync(execution, firstSegment, token);
                firstSegment = GetFirstSegment(execution.Plan.RoutePlan);
                using (lockLease)
                {
                await SendCheckedAsync(new VehicleCommand
                {
                    CommandId = execution.CommandId, VehicleId = execution.Plan.VehicleId, TaskId = execution.Plan.TaskId,
                    Type = VehicleCommandType.Move, RouteVersion = execution.Plan.RouteVersion,
                    RoutePointOffset = firstSegment.StartPointOffset, TotalRoutePoints = execution.Plan.Points.Count,
                    HasMorePoints = HasNextSegment(execution.Plan.RoutePlan, 0),
                    ContainerCode = execution.Plan.ContainerCode, Points = firstSegment.Points, StartPaused = startPaused
                }, token);
                accepted = true;
                lockLease.Commit();
                PublishCurrentState(execution.Plan.VehicleId);
                }
            }
            finally { execution.WindowGate.Release(); }
            var result = await _channel.WaitForCompletionAsync(plan.VehicleId, execution.CommandId, token);
            if (execution.RouteUpdateError is { } routeError)
                throw new InvalidOperationException($"路径分段下发失败：{routeError.Message}", routeError);
            token.ThrowIfCancellationRequested();
            if (result.Status != "COMPLETED") throw new InvalidOperationException(result.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 取消等待不代表车已停。发送 STOP 并确认完成后，任务服务才能释放车辆。
            if (accepted)
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
    public async Task<RcsTaskExecutionPlan> ReplanAsync(string taskId, CancellationToken token = default)
    {
        Execution execution;
        lock (_gate) execution = _current.Values.FirstOrDefault(x => x.Plan.TaskId == taskId) is { } current ? current
            : throw new InvalidOperationException("任务尚未开始车辆执行或已经结束。");
        await execution.WindowGate.WaitAsync(token);
        try
        {
            var oldPlan = execution.Plan;
            var state = Vehicle(oldPlan.VehicleId);
            if (state.Status != "Paused" || state.TaskId != taskId || string.IsNullOrWhiteSpace(state.PointCode))
                throw new InvalidOperationException("车辆需要暂停且有确认的位置，才能更新路径。");
            var remaining = oldPlan.Stops.Where(x => !state.CompletedStepIds.Contains(x.StepId, StringComparer.Ordinal)).ToArray();
            if (remaining.Length == 0) throw new InvalidOperationException("所有任务站点已经执行，无需重新规划。");
            var map = _maps.Current ?? throw new InvalidOperationException("当前地图未加载。");
            EnsureMap(map.MapCode, map.SceneName, oldPlan.Map.MapCode, oldPlan.Map.SceneName);
            _traffic.SetVehicleGoals(state.Id, remaining.Select(x => x.PointCode).ToArray());
            var plan = oldPlan with
            {
                Map = map, RoutePlan = _planner.Plan(map, _traffic.GetPlanningContext(state.Id), state.PointCode,
                    remaining, _algorithmSettings.Current, retainAnchor: true),
                RouteVersion = checked(state.RouteVersion + 1)
            };
            var firstSegment = GetFirstSegment(plan.RoutePlan);
            using var lockLease = await _traffic.AcquireRouteWindowAsync(plan.Map, plan.VehicleId, plan.TaskId,
                firstSegment.Points, firstSegment.StartPointOffset, plan.RouteVersion, token);
            await SendCheckedAsync(new VehicleCommand
            {
                CommandId = Guid.NewGuid().ToString("N"), VehicleId = plan.VehicleId, TaskId = taskId,
                Type = VehicleCommandType.UpdateRoute, RouteVersion = plan.RouteVersion,
                RoutePointOffset = firstSegment.StartPointOffset, TotalRoutePoints = plan.Points.Count,
                HasMorePoints = HasNextSegment(plan.RoutePlan, 0),
                ContainerCode = plan.ContainerCode, Points = firstSegment.Points,
                ExpectedPointCode = state.PointCode
            }, token);
            lockLease.Commit();
            PublishCurrentState(plan.VehicleId);
            execution.Plan = plan; execution.SegmentIndex = 0;
            execution.RouteVersion = plan.RouteVersion; execution.RouteUpdateError = null;
            return plan;
        }
        finally { execution.WindowGate.Release(); }
    }

    public void ValidateReset(string vehicleId, string pointCode)
    {
        Vehicle(vehicleId);
        if (!string.IsNullOrEmpty(pointCode) && _maps.Current?.Points.ContainsKey(pointCode) != true)
            throw new ArgumentException("重置站点不在当前可用地图中。");
    }

    public async Task ResetAsync(string vehicleId, string pointCode, CancellationToken token = default)
    {
        ValidateReset(vehicleId, pointCode);
        var execution = FindExecution(vehicleId);
        if (execution is not null) await execution.WindowGate.WaitAsync(token);
        try
        {
            var point = string.IsNullOrEmpty(pointCode) ? null : RcsTaskRoutePlanner.ToPoint(_maps.Current!.Points[pointCode]);
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
        ObserveVehiclePosition(state);
        if (state.Status is "Running" or "Paused")
            _traffic.ObserveProgress(state.Id, state.TaskId, state.RouteVersion, state.RouteIndex, state.PointCode);
        else ObserveStationaryPosition(state);
        _vehicleStateChanged?.Invoke(EnrichState(state));
        if (state.Status != "Running") return;
        Execution? execution;
        lock (_gate) _current.TryGetValue(state.Id, out execution);
        if (execution is not null && execution.Plan.TaskId == state.TaskId)
            _ = AdvanceRouteWindowAsync(execution);
    }

    private async Task AdvanceRouteWindowAsync(Execution execution)
    {
        try { await execution.WindowGate.WaitAsync(execution.Token); }
        catch (OperationCanceledException) when (execution.Token.IsCancellationRequested) { return; }
        try
        {
            if (execution.Token.IsCancellationRequested) return;
            var state = Vehicle(execution.Plan.VehicleId);
            if (state.Status != "Running" || state.TaskId != execution.Plan.TaskId
                || state.RouteVersion != execution.RouteVersion || state.RouteIndex < 1) return;
            var currentIndex = state.RouteIndex - 1;
            if (!HasNextSegment(execution.Plan.RoutePlan, execution.SegmentIndex)) return;
            var nextSegmentIndex = execution.SegmentIndex + 1;
            var nextSegment = execution.Plan.RoutePlan.Segments[nextSegmentIndex];
            // The algorithm encodes the configured advance threshold in the next segment's offset.
            if (currentIndex < nextSegment.StartPointOffset) return;
            var nextVersion = checked(execution.RouteVersion + 1);
            IAlgorithmRouteLease lockLease;
            if (!_traffic.TryAcquireRouteWindow(execution.Plan.Map, execution.Plan.VehicleId, execution.Plan.TaskId,
                nextSegment.Points, nextSegment.StartPointOffset, nextVersion, out var immediateLease, out _))
            {
                if (await TryAutomaticReplanAsync(execution, state)) return;
                lockLease = await _traffic.AcquireRouteWindowAsync(execution.Plan.Map, execution.Plan.VehicleId,
                    execution.Plan.TaskId, nextSegment.Points, nextSegment.StartPointOffset, nextVersion, execution.Token);
            }
            else lockLease = immediateLease!;
            using (lockLease)
            {
            var command = new VehicleCommand
            {
                CommandId = Guid.NewGuid().ToString("N"), VehicleId = execution.Plan.VehicleId,
                TaskId = execution.Plan.TaskId, Type = VehicleCommandType.SlideRoute, RouteVersion = nextVersion,
                RoutePointOffset = nextSegment.StartPointOffset, TotalRoutePoints = execution.Plan.Points.Count,
                HasMorePoints = HasNextSegment(execution.Plan.RoutePlan, nextSegmentIndex),
                ContainerCode = execution.Plan.ContainerCode, ExpectedPointCode = state.PointCode, Points = nextSegment.Points
            };
            var ack = await _channel.SendAsync(command, execution.Token);
            if (!ack.Accepted)
            {
                var latest = Vehicle(execution.Plan.VehicleId);
                if (latest.Status == "Running" && latest.TaskId == execution.Plan.TaskId
                    && latest.RouteVersion == execution.RouteVersion && latest.RouteIndex != state.RouteIndex)
                {
                    // The vehicle advanced during transport. A fresh state event will send a window anchored at its newer point.
                    return;
                }
                throw new InvalidOperationException(ack.Message);
            }
            lockLease.Commit();
            PublishCurrentState(execution.Plan.VehicleId);
            execution.SegmentIndex = nextSegmentIndex;
            execution.RouteVersion = nextVersion;
            _logger.LogInformation("算法路径段已续发 TaskId={TaskId} Vehicle={Vehicle} RouteVersion={Version} Segment={Segment}/{Count} Offset={Offset} Points={Points}",
                execution.Plan.TaskId, execution.Plan.VehicleId, nextVersion, nextSegmentIndex + 1,
                execution.Plan.RoutePlan.Segments.Count, nextSegment.StartPointOffset, nextSegment.Points.Count);
            }
        }
        catch (OperationCanceledException) when (execution.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            execution.RouteUpdateError = ex;
            _logger.LogError(ex, "续发车辆路径失败 TaskId={TaskId} Vehicle={Vehicle}", execution.Plan.TaskId, execution.Plan.VehicleId);
            try
            {
                await _channel.SendAsync(Control(execution.Plan.VehicleId, execution.Plan.TaskId, VehicleCommandType.Stop), CancellationToken.None);
            }
            catch (Exception stopError)
            {
                _logger.LogError(stopError, "续发失败后停止车辆也失败 TaskId={TaskId} Vehicle={Vehicle}", execution.Plan.TaskId, execution.Plan.VehicleId);
            }
        }
        finally { execution.WindowGate.Release(); }
    }

    private VehicleStateDto Vehicle(string id) => _channel.GetStates().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"车辆 {id} 不存在。");

    private async Task<bool> TryAutomaticReplanAsync(Execution execution, VehicleStateDto observedState)
    {
        if (observedState.Status != "Running" || string.IsNullOrWhiteSpace(observedState.PointCode)) return false;
        var vehicleId = execution.Plan.VehicleId;
        var taskId = execution.Plan.TaskId;
        await SendCheckedAsync(Control(vehicleId, taskId, VehicleCommandType.Pause), execution.Token);
        var routeUpdated = false;
        try
        {
            var state = Vehicle(vehicleId);
            if (state.Status != "Paused" || state.TaskId != taskId || string.IsNullOrWhiteSpace(state.PointCode))
                return false;
            var remaining = execution.Plan.Stops
                .Where(x => !state.CompletedStepIds.Contains(x.StepId, StringComparer.Ordinal)).ToArray();
            if (remaining.Length == 0) return false;

            var candidate = execution.Plan with
            {
                Stops = remaining,
                RoutePlan = _planner.Plan(execution.Plan.Map, _traffic.GetPlanningContext(vehicleId),
                    state.PointCode, remaining, _algorithmSettings.Current, retainAnchor: true),
                RouteVersion = checked(state.RouteVersion + 1)
            };
            var segment = GetFirstSegment(candidate.RoutePlan);
            var anchor = segment.Points.FirstOrDefault();
            // UPDATE_ROUTE requires the confirmed current position as a plain move anchor.
            if (anchor is null || !string.Equals(anchor.PointCode, state.PointCode, StringComparison.OrdinalIgnoreCase)
                || anchor.Action != VehiclePointAction.Move || anchor.StepId != "") return false;

            if (!_traffic.TryAcquireRouteWindow(candidate.Map, vehicleId, taskId, segment.Points,
                segment.StartPointOffset, candidate.RouteVersion, out var lease, out _)) return false;

            using (lease!)
            {
                var ack = await _channel.SendAsync(new VehicleCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"), VehicleId = vehicleId, TaskId = taskId,
                    Type = VehicleCommandType.UpdateRoute, RouteVersion = candidate.RouteVersion,
                    RoutePointOffset = segment.StartPointOffset, TotalRoutePoints = candidate.Points.Count,
                    HasMorePoints = HasNextSegment(candidate.RoutePlan, 0), ContainerCode = candidate.ContainerCode,
                    ExpectedPointCode = state.PointCode, Points = segment.Points
                }, execution.Token);
                if (!ack.Accepted) return false;

                lease!.Commit();
                execution.Plan = candidate;
                execution.SegmentIndex = 0;
                execution.RouteVersion = candidate.RouteVersion;
                execution.RouteUpdateError = null;
                _traffic.SetVehicleGoals(vehicleId, remaining.Select(x => x.PointCode).ToArray());
                routeUpdated = true;
            }

            PublishCurrentState(vehicleId);
            await SendCheckedAsync(Control(vehicleId, taskId, VehicleCommandType.Resume), execution.Token);
            _logger.LogInformation("算法根据车队位置及路径占用完成自动改路 TaskId={TaskId} Vehicle={Vehicle} RouteVersion={Version} Points={PointCount}",
                taskId, vehicleId, candidate.RouteVersion, candidate.Points.Count);
            return true;
        }
        catch (InvalidOperationException ex) when (!routeUpdated)
        {
            _logger.LogInformation(ex, "当前没有可立即预约的替代路线，车辆恢复原路线并等待资源 TaskId={TaskId} Vehicle={Vehicle}", taskId, vehicleId);
            return false;
        }
        finally
        {
            if (!routeUpdated)
            {
                var latest = _channel.GetStates().FirstOrDefault(x => string.Equals(x.Id, vehicleId, StringComparison.OrdinalIgnoreCase));
                if (latest?.Status == "Paused" && latest.TaskId == taskId)
                {
                    try { await SendCheckedAsync(Control(vehicleId, taskId, VehicleCommandType.Resume), CancellationToken.None); }
                    catch (Exception ex) { _logger.LogError(ex, "改路未完成且恢复原路线失败 TaskId={TaskId} Vehicle={Vehicle}", taskId, vehicleId); }
                }
            }
        }
    }

    private async Task<IAlgorithmRouteLease> AcquireFirstWindowAsync(
        Execution execution, RouteSegmentDto initialSegment, CancellationToken token)
    {
        var state = Vehicle(execution.Plan.VehicleId);
        var segment = initialSegment;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_traffic.TryAcquireRouteWindow(execution.Plan.Map, execution.Plan.VehicleId, execution.Plan.TaskId,
                segment.Points, segment.StartPointOffset, execution.Plan.RouteVersion, out var lease, out _)) return lease!;

            var context = _traffic.GetPlanningContext(execution.Plan.VehicleId);
            var candidate = execution.Plan with
            {
                RoutePlan = _planner.Plan(execution.Plan.Map, context, state.PointCode,
                    execution.Plan.Stops, _algorithmSettings.Current)
            };
            execution.Plan = candidate;
            segment = GetFirstSegment(candidate.RoutePlan);
        }

        return await _traffic.AcquireRouteWindowAsync(execution.Plan.Map, execution.Plan.VehicleId,
            execution.Plan.TaskId, segment.Points, segment.StartPointOffset, execution.Plan.RouteVersion, token);
    }

    private void ObserveStationaryPosition(VehicleStateDto state)
    {
        if (!_traffic.ObserveStationaryPosition(state.Id, state.PointCode) && !string.IsNullOrWhiteSpace(state.PointCode))
            _logger.LogError("车辆当前位置与另一车辆的地图锁冲突 Vehicle={Vehicle} Point={Point}", state.Id, state.PointCode);
    }

    private void SyncFleet(IReadOnlyList<VehicleStateDto> states)
    {
        foreach (var state in states)
        {
            ObserveVehiclePosition(state);
            if (state.Status is not ("Running" or "Paused")) ObserveStationaryPosition(state);
        }
    }

    private void ObserveVehiclePosition(VehicleStateDto state) => _traffic.ObserveVehiclePosition(
        new AlgorithmVehiclePositionDto(state.Id, state.PointCode,
            state.Status is "Running" or "Paused", DateTimeOffset.UtcNow));

    private VehicleStateDto EnrichState(VehicleStateDto state)
    {
        var points = _traffic.GetLockedPoints(state.Id);
        var lines = _traffic.GetLockedLines(state.Id);
        return state with
        {
            LockPointCode = state.Status is "Running" or "Paused" && points.Count > 0
                ? points.Last() : "",
            LockedPointCodes = points,
            LockedLineCodes = lines
        };
    }

    private void PublishCurrentState(string vehicleId)
    {
        var state = _channel.GetStates().FirstOrDefault(x => string.Equals(x.Id, vehicleId, StringComparison.OrdinalIgnoreCase));
        if (state is not null) _vehicleStateChanged?.Invoke(EnrichState(state));
    }

    private Execution? FindExecution(string vehicleId)
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

    private static RouteSegmentDto GetFirstSegment(AlgorithmRoutePlanDto routePlan) => routePlan.Segments.FirstOrDefault()
        ?? throw new InvalidOperationException("路径算法未返回可下发的路径段。");

    private static bool HasNextSegment(AlgorithmRoutePlanDto routePlan, int currentSegmentIndex) =>
        currentSegmentIndex + 1 < routePlan.Segments.Count;

    public void Dispose() => _channel.StateChanged -= OnVehicleStateChanged;

    private sealed class Execution(RcsTaskExecutionPlan plan, string commandId, CancellationToken token)
    {
        public RcsTaskExecutionPlan Plan { get; set; } = plan;
        public string CommandId { get; } = commandId;
        public CancellationToken Token { get; } = token;
        public SemaphoreSlim WindowGate { get; } = new(1, 1);
        public int SegmentIndex { get; set; }
        public int RouteVersion { get; set; } = plan.RouteVersion;
        public Exception? RouteUpdateError { get; set; }
    }
}
