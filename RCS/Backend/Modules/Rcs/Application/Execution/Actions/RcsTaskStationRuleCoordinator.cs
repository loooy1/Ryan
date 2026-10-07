using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Protocol;
using Contracts.Rcs.StationBusiness;
using RCSBackend.Modules.Rcs.Application.StationBusiness;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>Coordinates station rules with route windows and task progress.</summary>
public sealed class RcsTaskStationRuleCoordinator(
    RcsStationBusinessRuleService rules,
    ILogger<RcsTaskStationRuleCoordinator> logger)
{
    public void ValidateWaitPointGates(RcsTaskExecutionPlan plan)
    {
        var path = plan.RoutePointCodes;
        for (var targetIndex = 1; targetIndex < path.Count; targetIndex++)
        {
            var gatedRules = rules.Get(plan.Map.MapCode, path[targetIndex])
                .Where(x => x.IsEnabled && x.Event == RcsStationEvents.BeforeEnter && !string.IsNullOrWhiteSpace(x.WaitPointCode));
            foreach (var rule in gatedRules)
                if (!path.Take(targetIndex).Contains(rule.WaitPointCode, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"业务站点 {rule.PointCode} 的等待站点 {rule.WaitPointCode} 不在当前路径上，无法安全等待放行。");
        }
    }

    public async Task<bool> WaitForGateAtCurrentPointAsync(RcsTaskExecutionPlan plan,
        HashSet<int> authorizedEntryIndices, int currentIndex, CancellationToken token)
    {
        if (currentIndex < 0 || currentIndex >= plan.RoutePointCodes.Count) return false;
        var pointCode = plan.RoutePointCodes[currentIndex];
        var reachedGate = false;
        for (var targetIndex = currentIndex + 1; targetIndex < plan.RoutePointCodes.Count; targetIndex++)
        {
            var targetCode = plan.RoutePointCodes[targetIndex];
            var gates = rules.Get(plan.Map.MapCode, targetCode)
                .Where(x => x.IsEnabled && x.Event == RcsStationEvents.BeforeEnter
                    && string.Equals(x.WaitPointCode, pointCode, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (gates.Length == 0) continue;
            reachedGate = true;
            if (authorizedEntryIndices.Contains(targetIndex)) continue;
            await rules.WaitForGateAsync(new(plan.TaskId, plan.VehicleId, plan.Map.MapCode, plan.Warehouse,
                targetCode, RcsStationEvents.BeforeEnter, plan.ContainerCode, "", pointCode), token);
            authorizedEntryIndices.Add(targetIndex);
        }
        return reachedGate;
    }

    public VehicleRoutePoint[] LimitWindowAtWaitPoint(RcsTaskExecutionPlan plan,
        HashSet<int> authorizedEntryIndices, int startOffset, IReadOnlyList<VehicleRoutePoint> points)
    {
        for (var relativeTarget = 1; relativeTarget < points.Count; relativeTarget++)
        {
            var targetIndex = startOffset + relativeTarget;
            if (targetIndex >= plan.RoutePointCodes.Count || authorizedEntryIndices.Contains(targetIndex)) continue;
            var targetCode = plan.RoutePointCodes[targetIndex];
            foreach (var rule in rules.Get(plan.Map.MapCode, targetCode)
                .Where(x => x.IsEnabled && x.Event == RcsStationEvents.BeforeEnter && !string.IsNullOrWhiteSpace(x.WaitPointCode)))
            {
                var waitIndex = plan.RoutePointCodes.Take(targetIndex)
                    .Select((code, index) => (code, index))
                    .Where(x => string.Equals(x.code, rule.WaitPointCode, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.index).DefaultIfEmpty(-1).Max();
                if (waitIndex < startOffset)
                    throw new InvalidOperationException($"车辆已越过业务站点 {targetCode} 配置的等待站点 {rule.WaitPointCode}，本次路径无法安全执行。");
                if (waitIndex == startOffset) continue;
                if (waitIndex - startOffset < points.Count) return points.Take(waitIndex - startOffset + 1).ToArray();
            }
        }
        return points.ToArray();
    }

    public Task RunBeforeLeaveAsync(RcsTaskExecutionPlan plan, string pointCode, CancellationToken token) =>
        RunAsync(plan, pointCode, RcsStationEvents.BeforeLeave, "", token);

    public async Task RunBeforeEnterForSegmentAsync(RcsTaskExecutionPlan plan, HashSet<int> checkedEntryIndices,
        IReadOnlyList<VehicleRoutePoint> points, int startOffset, CancellationToken token)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var routeIndex = startOffset + i;
            if (checkedEntryIndices.Contains(routeIndex)) continue;
            var rulesForPoint = rules.Get(plan.Map.MapCode, points[i].PointCode)
                .Where(x => x.IsEnabled && x.Event == RcsStationEvents.BeforeEnter && string.IsNullOrWhiteSpace(x.WaitPointCode)).ToArray();
            if (rulesForPoint.Length == 0) continue;
            await RunAsync(plan, points[i].PointCode, RcsStationEvents.BeforeEnter, "", token);
            checkedEntryIndices.Add(routeIndex);
        }
    }

    public Task RunAsync(RcsTaskExecutionPlan plan, string pointCode, string eventCode, string action, CancellationToken token) =>
        rules.ExecuteAsync(new(plan.TaskId, plan.VehicleId, plan.Map.MapCode,
            plan.Warehouse, pointCode, eventCode, plan.ContainerCode, action), token);

    public async Task RunSafelyAsync(RcsTaskExecutionPlan plan, string pointCode, string eventCode,
        string action, CancellationToken token)
    {
        try { await RunAsync(plan, pointCode, eventCode, action, token); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "站点异步业务规则执行失败 TaskId={TaskId} Vehicle={Vehicle} Point={Point} Event={Event}",
                plan.TaskId, plan.VehicleId, pointCode, eventCode);
        }
    }
}
