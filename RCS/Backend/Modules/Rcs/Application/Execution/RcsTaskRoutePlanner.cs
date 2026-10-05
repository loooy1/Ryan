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
        var carry = request.TaskType.ToUpperInvariant() is "INBOUND" or "OUTBOUND";
        if (carry && (request.StationCodes.Count < 2 || string.IsNullOrWhiteSpace(request.ContainerCode)))
            throw new ArgumentException("INBOUND/OUTBOUND 至少需要两个站点及 ContainerCode。");
        return request.StationCodes.Select((code, index) => new RcsTaskStop($"{request.TaskId}:{index}", code,
            carry && index == 0 ? VehiclePointAction.Fetch
            : carry && index == request.StationCodes.Count - 1 ? VehiclePointAction.Put : VehiclePointAction.Move)).ToArray();
    }

    public AlgorithmRoutePlanDto Plan(RcsMapSnapshot map, AlgorithmPlanningContextDto fleet, string current,
        IReadOnlyList<RcsTaskStop> stops, AlgorithmSettingsDto settings, bool retainAnchor = false)
    {
        var route = algorithm.Plan(map, fleet, current,
            stops.Select(x => new AlgorithmRouteStopDto(x.PointCode)).ToArray(), settings, retainAnchor);
        var points = route.TotalPath.Select(point => point with
        {
            Action = VehiclePointAction.Move,
            StepId = ""
        }).ToArray();

        var searchFrom = 0;
        foreach (var stop in stops)
        {
            var index = Array.FindIndex(points, searchFrom,
                point => string.Equals(point.PointCode, stop.PointCode, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw new InvalidOperationException($"算法返回的路径没有经过目标站点 {stop.PointCode}。");
            searchFrom = index + 1;
        }

        // Path commands contain only movement points. RCS sends station actions as separate commands.
        var segments = route.Segments.Select(segment => segment with
        {
            Points = points.Skip(segment.StartPointOffset).Take(segment.Points.Count).ToArray()
        }).ToArray();
        return route with { TotalPath = points, Segments = segments };
    }

    public static VehicleRoutePoint ToPoint(RcsMapNode node) => new()
        { PointCode = node.PointCode, X = node.X, Y = node.Y, Z = node.Z, Floor = node.Floor };
}
