using Contracts.Rcs.Map;
using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Protocol;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>规划和执行共用同一地图快照引用，避免运行期间换图影响已规划路径。</summary>
public sealed record RcsTaskExecutionPlan(string TaskId, string VehicleId, RcsMapSnapshot Map,
    AlgorithmRoutePlanDto RoutePlan, IReadOnlyList<RcsTaskStop> Stops, string ContainerCode, int RouteVersion = 1,
    bool SyncInventory = false, IReadOnlyList<RcsTaskStop>? DeferredStops = null,
    IReadOnlyList<RcsTaskStop>? AllStops = null, string Warehouse = "")
{
    public IReadOnlyList<VehicleRoutePoint> Points => RoutePlan.TotalPath;
    public IReadOnlyList<string> RoutePointCodes => Points.Select(x => x.PointCode).ToArray();
    public IReadOnlyList<RcsTaskStop> RemainingStageStops => DeferredStops ?? Array.Empty<RcsTaskStop>();
    public IReadOnlyList<RcsTaskStop> TaskStops => AllStops ?? Stops;
}

/// <summary>业务站点动作与算法插入的移动点分开；StepId 在各路径版本间稳定。</summary>
public sealed record RcsTaskStop(string StepId, string PointCode, string Action);
