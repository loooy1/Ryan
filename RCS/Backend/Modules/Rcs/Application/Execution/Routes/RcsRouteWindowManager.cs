using Contracts.Rcs.Protocol;
using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Vehicle;
using Rcs.Algorithms;
using Rcs.Algorithms.Traffic;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;
using RCSBackend.Modules.Rcs.Application.Maps;
using RCSBackend.Modules.Rcs.Protocol;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>路径首段、续发、锁窗口和重规划的唯一协调入口。</summary>
public sealed class RcsRouteWindowManager(
    RcsMapCache maps, RcsTaskRoutePlanner planner, RcsAlgorithmSettingsService algorithmSettings,
    IMultiVehicleTrafficCoordinator traffic, IVehicleCommandChannel channel,
    RcsTaskStationRuleCoordinator stationRuleCoordinator, ILogger<RcsRouteWindowManager> logger)
{
    private readonly RcsMapCache _maps = maps;
    private readonly RcsTaskRoutePlanner _planner = planner;
    private readonly RcsAlgorithmSettingsService _algorithmSettings = algorithmSettings;
    private readonly IMultiVehicleTrafficCoordinator _traffic = traffic;
    private readonly IVehicleCommandChannel _channel = channel;
    private readonly RcsTaskStationRuleCoordinator _stationRuleCoordinator = stationRuleCoordinator;
    private readonly ILogger<RcsRouteWindowManager> _logger = logger;

    public async Task ExecuteLegAsync(RcsTaskExecutionSession execution, bool startPaused, Action<string> publish, CancellationToken token)
    {
        execution.CommandId = Guid.NewGuid().ToString("N");
        execution.SegmentIndex = 0;
        execution.RouteVersion = execution.Plan.RouteVersion;
        execution.RouteUpdateError = null;
        await execution.WindowGate.WaitAsync(token);
        try
        {
            var firstSegment = GetFirstSegment(execution.Plan.RoutePlan);
            _stationRuleCoordinator.ValidateWaitPointGates(execution.Plan);
            execution.AuthorizedEntryIndices.Clear();
            execution.CheckedEntryIndices.Clear();
            await _stationRuleCoordinator.WaitForGateAtCurrentPointAsync(execution.Plan, execution.AuthorizedEntryIndices, 0, token);
            firstSegment = firstSegment with { Points = _stationRuleCoordinator.LimitWindowAtWaitPoint(execution.Plan, execution.AuthorizedEntryIndices, firstSegment.StartPointOffset, firstSegment.Points) };
            await _stationRuleCoordinator.RunBeforeLeaveAsync(execution.Plan, Vehicle(execution.Plan.VehicleId).PointCode, token);
            await _stationRuleCoordinator.RunBeforeEnterForSegmentAsync(execution.Plan, execution.CheckedEntryIndices, firstSegment.Points, firstSegment.StartPointOffset, token);
            _logger.LogInformation("开始下发车辆路径阶段 TaskId={TaskId} Vehicle={Vehicle} RouteVersion={Version} From={From} To={To} Points={Points}",
                execution.Plan.TaskId, execution.Plan.VehicleId, execution.Plan.RouteVersion,
                Vehicle(execution.Plan.VehicleId).PointCode, execution.Plan.Stops.LastOrDefault()?.PointCode ?? "",
                execution.Plan.RoutePointCodes.Count);
            var lockLease = await AcquireFirstWindowAsync(execution, firstSegment, token);
            _stationRuleCoordinator.ValidateWaitPointGates(execution.Plan);
            await _stationRuleCoordinator.WaitForGateAtCurrentPointAsync(execution.Plan, execution.AuthorizedEntryIndices, 0, token);
            var refreshedSegment = GetFirstSegment(execution.Plan.RoutePlan);
            firstSegment = refreshedSegment with
            { Points = _stationRuleCoordinator.LimitWindowAtWaitPoint(execution.Plan, execution.AuthorizedEntryIndices, refreshedSegment.StartPointOffset, refreshedSegment.Points) };
            await _stationRuleCoordinator.RunBeforeEnterForSegmentAsync(execution.Plan, execution.CheckedEntryIndices, firstSegment.Points, firstSegment.StartPointOffset, token);
            using (lockLease)
            {
                await SendCheckedAsync(new VehicleCommand
                {
                    CommandId = execution.CommandId, VehicleId = execution.Plan.VehicleId, TaskId = execution.Plan.TaskId,
                    Type = VehicleCommandType.Move, RouteVersion = execution.Plan.RouteVersion,
                    RoutePointOffset = firstSegment.StartPointOffset, TotalRoutePoints = execution.Plan.Points.Count,
                    HasMorePoints = firstSegment.StartPointOffset + firstSegment.Points.Count < execution.Plan.Points.Count,
                    ContainerCode = execution.Plan.ContainerCode, Points = firstSegment.Points, StartPaused = startPaused
                }, token);
                execution.HasAcceptedCommand = true;
                lockLease.Commit();
                _logger.LogInformation("车辆已确认接收路径阶段 TaskId={TaskId} Vehicle={Vehicle} RouteVersion={Version} CommandId={CommandId}",
                    execution.Plan.TaskId, execution.Plan.VehicleId, execution.Plan.RouteVersion, execution.CommandId);
                publish(execution.Plan.VehicleId);
            }
        }
        finally { execution.WindowGate.Release(); }

        var commandId = execution.CommandId;
        var result = await _channel.WaitForCompletionAsync(execution.Plan.VehicleId, commandId, token);
        if (execution.RouteUpdateError is { } routeError)
            throw new InvalidOperationException($"路径分段下发失败：{routeError.Message}", routeError);
        token.ThrowIfCancellationRequested();
        if (result.Status != "COMPLETED") throw new InvalidOperationException(result.Message);
    }

    public async Task<RcsTaskExecutionPlan> ReplanAsync(RcsTaskExecutionSession execution, string taskId, Action<string> publish, CancellationToken token = default)
    {
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
            execution.Plan = plan;
            try
            {
                _stationRuleCoordinator.ValidateWaitPointGates(execution.Plan);
                execution.AuthorizedEntryIndices.Clear(); execution.CheckedEntryIndices.Clear();
                await _stationRuleCoordinator.WaitForGateAtCurrentPointAsync(execution.Plan, execution.AuthorizedEntryIndices, 0, token);
                var rawSegment = GetFirstSegment(plan.RoutePlan);
                var firstSegment = rawSegment with { Points = _stationRuleCoordinator.LimitWindowAtWaitPoint(execution.Plan, execution.AuthorizedEntryIndices, rawSegment.StartPointOffset, rawSegment.Points) };
                await _stationRuleCoordinator.RunBeforeLeaveAsync(execution.Plan, state.PointCode, token);
                await _stationRuleCoordinator.RunBeforeEnterForSegmentAsync(execution.Plan, execution.CheckedEntryIndices, firstSegment.Points, firstSegment.StartPointOffset, token);
                using var lockLease = await _traffic.AcquireRouteWindowAsync(plan.Map, plan.VehicleId, plan.TaskId,
                    PointCodes(firstSegment.Points), firstSegment.StartPointOffset, plan.RouteVersion, token);
                await SendCheckedAsync(new VehicleCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"), VehicleId = plan.VehicleId, TaskId = taskId,
                    Type = VehicleCommandType.UpdateRoute, RouteVersion = plan.RouteVersion,
                    RoutePointOffset = firstSegment.StartPointOffset, TotalRoutePoints = plan.Points.Count,
                    HasMorePoints = firstSegment.StartPointOffset + firstSegment.Points.Count < plan.Points.Count,
                    ContainerCode = plan.ContainerCode, Points = firstSegment.Points,
                    ExpectedPointCode = state.PointCode
                }, token);
                lockLease.Commit();
                publish(plan.VehicleId);
                execution.SegmentIndex = 0;
                execution.RouteVersion = plan.RouteVersion; execution.RouteUpdateError = null;
                return plan;
            }
            catch { execution.Plan = oldPlan; throw; }
        }
        finally { execution.WindowGate.Release(); }
    }

    public async Task AdvanceAsync(RcsTaskExecutionSession execution, Action<string> publish)
    {
        try { await execution.WindowGate.WaitAsync(execution.Token); }
        catch (OperationCanceledException) when (execution.Token.IsCancellationRequested) { return; }
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (execution.Token.IsCancellationRequested) return;
                var state = Vehicle(execution.Plan.VehicleId);
                if (state.Status != "Running" || state.TaskId != execution.Plan.TaskId
                    || state.RouteVersion != execution.RouteVersion || state.RouteIndex < 1) return;
                var currentIndex = state.RouteIndex - 1;
                var nextSegmentIndex = execution.SegmentIndex + 1;
                if (currentIndex >= execution.Plan.Points.Count - 1) return;
                var nextSegment = GetSegment(execution, nextSegmentIndex, currentIndex);
                // The algorithm's segment offset is the advance threshold. If the vehicle has already
                // passed that anchor, rebase the same size window on its latest confirmed point.
                var reachedBusinessWaitPoint = await _stationRuleCoordinator.WaitForGateAtCurrentPointAsync(execution.Plan, execution.AuthorizedEntryIndices, currentIndex, execution.Token);
                if (currentIndex < nextSegment.StartPointOffset && !reachedBusinessWaitPoint) return;
                var windowPoints = execution.Plan.Points.Skip(currentIndex).Take(nextSegment.Points.Count).ToArray();
                windowPoints = _stationRuleCoordinator.LimitWindowAtWaitPoint(execution.Plan, execution.AuthorizedEntryIndices, currentIndex, windowPoints);
                if (windowPoints.Length == 0) return;
                await _stationRuleCoordinator.RunBeforeLeaveAsync(execution.Plan, state.PointCode, execution.Token);
                await _stationRuleCoordinator.RunBeforeEnterForSegmentAsync(execution.Plan, execution.CheckedEntryIndices, windowPoints, currentIndex, execution.Token);
                var nextVersion = checked(execution.RouteVersion + 1);
                IAlgorithmRouteLease lockLease;
                if (!_traffic.TryAcquireRouteWindow(execution.Plan.Map, execution.Plan.VehicleId, execution.Plan.TaskId,
                    PointCodes(windowPoints), currentIndex, nextVersion, out var immediateLease, out _))
                {
                    if (await TryAutomaticReplanAsync(execution, state, publish)) return;
                    lockLease = await _traffic.AcquireRouteWindowAsync(execution.Plan.Map, execution.Plan.VehicleId,
                        execution.Plan.TaskId, PointCodes(windowPoints), currentIndex, nextVersion, execution.Token);
                }
                else lockLease = immediateLease!;
                using (lockLease)
                {
                    // Acquiring traffic locks can wait while this vehicle continues on its current
                    // window. Never send a continuation whose anchor is stale by the time it is ready.
                    var latest = Vehicle(execution.Plan.VehicleId);
                    if (latest.Status != "Running" || latest.TaskId != execution.Plan.TaskId
                        || latest.RouteVersion != execution.RouteVersion) return;
                    if (latest.RouteIndex != state.RouteIndex || latest.PointCode != state.PointCode)
                        continue;

                    var command = new VehicleCommand
                    {
                        CommandId = Guid.NewGuid().ToString("N"), VehicleId = execution.Plan.VehicleId,
                        TaskId = execution.Plan.TaskId, Type = VehicleCommandType.SlideRoute, RouteVersion = nextVersion,
                        RoutePointOffset = currentIndex, TotalRoutePoints = execution.Plan.Points.Count,
                        HasMorePoints = currentIndex + windowPoints.Length < execution.Plan.Points.Count,
                        ContainerCode = execution.Plan.ContainerCode, ExpectedPointCode = state.PointCode, Points = windowPoints
                    };
                    var ack = await _channel.SendAsync(command, execution.Token);
                    if (!ack.Accepted)
                    {
                        var afterSend = Vehicle(execution.Plan.VehicleId);
                        if (afterSend.Status == "Running" && afterSend.TaskId == execution.Plan.TaskId
                            && afterSend.RouteVersion == execution.RouteVersion
                            && (afterSend.RouteIndex != state.RouteIndex || afterSend.PointCode != state.PointCode))
                        {
                            // The vehicle advanced during transport; retry using its confirmed point.
                            continue;
                        }
                        throw new InvalidOperationException(ack.Message);
                    }
                    lockLease.Commit();
                    publish(execution.Plan.VehicleId);
                    execution.SegmentIndex = nextSegmentIndex;
                    execution.RouteVersion = nextVersion;
                    _logger.LogInformation("算法路径段已续发 TaskId={TaskId} Vehicle={Vehicle} RouteVersion={Version} Segment={Segment}/{Count} Offset={Offset} Points={Points}",
                        execution.Plan.TaskId, execution.Plan.VehicleId, nextVersion, nextSegmentIndex + 1,
                        execution.Plan.RoutePlan.Segments.Count, currentIndex, windowPoints.Length);
                    return;
                }
            }
            _logger.LogDebug("续发时车辆连续前进，等待新位置事件 TaskId={TaskId} Vehicle={Vehicle}",
                execution.Plan.TaskId, execution.Plan.VehicleId);
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

    private RcsVehicleRouteSegment GetSegment(RcsTaskExecutionSession execution, int index, int currentIndex)
    {
        if (index < execution.Plan.RoutePlan.Segments.Count) return execution.Plan.RoutePlan.Segments[index];
        var settings = _algorithmSettings.Current;
        var start = Math.Min(currentIndex + settings.AdvanceAfterPoints, execution.Plan.Points.Count - 1);
        return new RcsVehicleRouteSegment
        {
            StartPointOffset = start,
            Points = execution.Plan.Points.Skip(currentIndex).Take(settings.SegmentPointCount).ToArray()
        };
    }

    private async Task<bool> TryAutomaticReplanAsync(RcsTaskExecutionSession execution, VehicleStateDto observedState, Action<string> publish)
    {
        if (observedState.Status != "Running" || string.IsNullOrWhiteSpace(observedState.PointCode)) return false;
        var vehicleId = execution.Plan.VehicleId;
        var taskId = execution.Plan.TaskId;
        var originalPlan = execution.Plan;
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
            execution.Plan = candidate;
            _stationRuleCoordinator.ValidateWaitPointGates(execution.Plan);
            execution.AuthorizedEntryIndices.Clear(); execution.CheckedEntryIndices.Clear();
            await _stationRuleCoordinator.WaitForGateAtCurrentPointAsync(execution.Plan, execution.AuthorizedEntryIndices, 0, execution.Token);
            var rawSegment = GetFirstSegment(candidate.RoutePlan);
            var segment = rawSegment with { Points = _stationRuleCoordinator.LimitWindowAtWaitPoint(execution.Plan, execution.AuthorizedEntryIndices, rawSegment.StartPointOffset, rawSegment.Points) };
            var anchor = segment.Points.FirstOrDefault();
            // UPDATE_ROUTE requires the confirmed current position as a plain move anchor.
            if (anchor is null || !string.Equals(anchor.PointCode, state.PointCode, StringComparison.OrdinalIgnoreCase)
                || anchor.Action != VehiclePointAction.Move || anchor.StepId != "") return false;

            await _stationRuleCoordinator.RunBeforeLeaveAsync(execution.Plan, state.PointCode, execution.Token);
            await _stationRuleCoordinator.RunBeforeEnterForSegmentAsync(execution.Plan, execution.CheckedEntryIndices, segment.Points, segment.StartPointOffset, execution.Token);

            if (!_traffic.TryAcquireRouteWindow(candidate.Map, vehicleId, taskId, PointCodes(segment.Points),
                segment.StartPointOffset, candidate.RouteVersion, out var lease, out _)) return false;

            using (lease!)
            {
                var ack = await _channel.SendAsync(new VehicleCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"), VehicleId = vehicleId, TaskId = taskId,
                    Type = VehicleCommandType.UpdateRoute, RouteVersion = candidate.RouteVersion,
                    RoutePointOffset = segment.StartPointOffset, TotalRoutePoints = candidate.Points.Count,
                    HasMorePoints = segment.StartPointOffset + segment.Points.Count < candidate.Points.Count, ContainerCode = candidate.ContainerCode,
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

            publish(vehicleId);
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
                execution.Plan = originalPlan;
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
        RcsTaskExecutionSession execution, RcsVehicleRouteSegment initialSegment, CancellationToken token)
    {
        var state = Vehicle(execution.Plan.VehicleId);
        var segment = initialSegment;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_traffic.TryAcquireRouteWindow(execution.Plan.Map, execution.Plan.VehicleId, execution.Plan.TaskId,
                PointCodes(segment.Points), segment.StartPointOffset, execution.Plan.RouteVersion, out var lease, out _)) return lease!;

            var context = _traffic.GetPlanningContext(execution.Plan.VehicleId);
            var candidate = execution.Plan with
            {
                RoutePlan = _planner.Plan(execution.Plan.Map, context, state.PointCode,
                    execution.Plan.Stops, _algorithmSettings.Current)
            };
            execution.Plan = candidate;
            var candidateFirst = GetFirstSegment(candidate.RoutePlan);
            segment = candidateFirst with { Points = _stationRuleCoordinator.LimitWindowAtWaitPoint(execution.Plan, execution.AuthorizedEntryIndices, candidateFirst.StartPointOffset, candidateFirst.Points) };
        }

        return await _traffic.AcquireRouteWindowAsync(execution.Plan.Map, execution.Plan.VehicleId,
            execution.Plan.TaskId, PointCodes(segment.Points), segment.StartPointOffset, execution.Plan.RouteVersion, token);
    }

    private VehicleStateDto Vehicle(string id) => _channel.GetStates().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"车辆 {id} 不存在。");

    private static string[] PointCodes(IReadOnlyList<VehicleRoutePoint> points) =>
        points.Select(point => point.PointCode).ToArray();

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

    private static RcsVehicleRouteSegment GetFirstSegment(RcsVehicleRoutePlan routePlan) => routePlan.Segments.FirstOrDefault()
        ?? throw new InvalidOperationException("路径算法未返回可下发的路径段。");
}
