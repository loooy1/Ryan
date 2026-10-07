using Contracts.Rcs.Map;
using Contracts.Rcs.Protocol;
using Contracts.Rcs.Algorithm;
using Rcs.Algorithms;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>使用缓存图引用计算纯移动路径；站点取放动作由协议层独立下发。</summary>
public sealed class RcsTaskRoutePlanner(IPathPlanningAlgorithm algorithm)
{
    public static IReadOnlyList<RcsTaskStop> CreateStops(RcsTaskExecutionRequest request)
    {
        if (request.StationActions is { Count: > 0 } actions)
            return request.StationCodes.Select((code, index) => new RcsTaskStop(
                $"{request.TaskId}:{index}", code, actions[index])).ToArray();
        // Keep old stored tasks executable; the upstream validator now accepts only Auto_Carry.
        var carry = request.TaskType.ToUpperInvariant() is "AUTO_CARRY" or "INBOUND" or "OUTBOUND";
        if (carry && (request.StationCodes.Count < 2 || string.IsNullOrWhiteSpace(request.ContainerCode)))
            throw new ArgumentException("Auto_Carry 搬运任务至少需要两个站点及 ContainerCode。");
        return request.StationCodes.Select((code, index) => new RcsTaskStop($"{request.TaskId}:{index}", code,
            carry && index == 0 ? VehiclePointAction.Fetch
            : carry && index == request.StationCodes.Count - 1 ? VehiclePointAction.Put : VehiclePointAction.Move)).ToArray();
    }

    public RcsVehicleRoutePlan Plan(RcsMapSnapshot map, AlgorithmPlanningContextDto fleet, string current,
        IReadOnlyList<RcsTaskStop> stops, AlgorithmSettingsDto settings, bool retainAnchor = false)
    {
        var resolvedStops = stops.Select(stop => map.TryGetPoint(stop.PointCode, out var node)
            ? stop with { PointCode = node.PointCode }
            : throw new ArgumentException($"任务站点 {stop.PointCode} 不存在或楼层与地图 Z 不匹配。")).ToArray();
        var route = algorithm.Plan(map, fleet, current,
            resolvedStops.Select(x => new AlgorithmRouteStopDto(x.PointCode)).ToArray(), settings, retainAnchor);
        var points = route.TotalPath.Select(point => new VehicleRoutePoint
        {
            PointCode = point.PointCode, X = point.X, Y = point.Y, Z = point.Z,
            Action = VehiclePointAction.Move, StepId = ""
        }).ToArray();

        var searchFrom = 0;
        foreach (var stop in resolvedStops)
        {
            var index = Array.FindIndex(points, searchFrom,
                point => string.Equals(point.PointCode, stop.PointCode, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new InvalidOperationException($"算法返回的路径没有经过目标站点 {stop.PointCode}。");
            searchFrom = index + 1;
        }

        // Path commands contain only movement points. RCS sends station actions as separate commands.
        var segments = route.Segments.Select(segment => new RcsVehicleRouteSegment
        {
            StartPointOffset = segment.StartPointOffset,
            Points = points.Skip(segment.StartPointOffset).Take(segment.Points.Count).ToArray()
        }).ToArray();
        return new RcsVehicleRoutePlan { TotalPath = points, Segments = segments };
    }

    public static VehicleRoutePoint ToPoint(RcsMapNode node) => new()
        { PointCode = node.PointCode, X = node.X, Y = node.Y, Z = node.Z };
}
