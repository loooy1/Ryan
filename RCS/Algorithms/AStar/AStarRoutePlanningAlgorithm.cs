using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Map;
using Contracts.Rcs.Protocol;
using Rcs.Algorithms.AStar;

namespace Rcs.Algorithms;

/// <summary>A* path construction and rolling route segmentation live together behind the algorithm contract.</summary>
public sealed class AStarRoutePlanningAlgorithm(IAStarPathfinder pathfinder) : IPathPlanningAlgorithm
{
    public AlgorithmRoutePlanDto Plan(RcsMapSnapshot map, AlgorithmPlanningContextDto fleet, string currentPointCode,
        IReadOnlyList<AlgorithmRouteStopDto> stops, AlgorithmSettingsDto settings, bool retainAnchor = false)
    {
        Validate(settings);
        var route = new List<VehicleRoutePoint>();
        if (!string.IsNullOrWhiteSpace(currentPointCode))
        {
            if (!map.Points.TryGetValue(currentPointCode, out var current))
                throw new InvalidOperationException("车辆当前位置不在当前可用地图中，请先确认地图与车辆位置。");
            route.Add(ToPoint(current));
        }

        var blockedPoints = fleet.ReservedPointCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockedLines = fleet.ReservedLineCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in fleet.Vehicles.Where(x => !string.Equals(x.VehicleId, fleet.VehicleId, StringComparison.OrdinalIgnoreCase)))
            if (!string.IsNullOrWhiteSpace(vehicle.CurrentPointCode)) blockedPoints.Add(vehicle.CurrentPointCode);

        var stopIndex = 0;
        foreach (var stop in stops)
        {
            if (!map.Points.TryGetValue(stop.PointCode, out var target))
                throw new InvalidOperationException($"任务站点 {stop.PointCode} 不存在或已禁用。");
            if (route.Count == 0) route.Add(ToPoint(target));
            else if (string.Equals(route[^1].PointCode, target.PointCode, StringComparison.OrdinalIgnoreCase)
                && stopIndex > 0
                && string.Equals(stops[stopIndex - 1].PointCode, target.PointCode, StringComparison.OrdinalIgnoreCase))
            {
                // Repeated consecutive goals represent two ordered visits to the same physical station.
                route.Add(ToPoint(target));
            }
            else
            {
                var leg = pathfinder.FindPath(map, route[^1].PointCode, stop.PointCode, blockedPoints, blockedLines);
                // If every path is temporarily occupied (for example the target is another vehicle's current point),
                // retain the valid route. The traffic coordinator will hold it until resources are released.
                if (!leg.Found) leg = pathfinder.FindPath(map, route[^1].PointCode, stop.PointCode);
                if (!leg.Found) throw new InvalidOperationException($"{route[^1].PointCode} → {stop.PointCode}：{leg.Message}");
                route.AddRange(leg.PointCodes.Skip(1).Select(code => ToPoint(map.Points[code])));
            }
            stopIndex++;
        }

        var totalPath = route.ToArray();
        var segments = BuildSegments(totalPath, settings);
        return new AlgorithmRoutePlanDto { TotalPath = totalPath, Segments = segments };
    }

    private static IReadOnlyList<RouteSegmentDto> BuildSegments(IReadOnlyList<VehicleRoutePoint> path, AlgorithmSettingsDto settings)
    {
        if (path.Count == 0) return Array.Empty<RouteSegmentDto>();
        var segments = new List<RouteSegmentDto>();
        for (var start = 0; start < path.Count; start += settings.AdvanceAfterPoints)
        {
            segments.Add(new RouteSegmentDto
            {
                StartPointOffset = start,
                Points = path.Skip(start).Take(settings.SegmentPointCount).ToArray()
            });
            if (start + settings.SegmentPointCount >= path.Count) break;
        }
        return segments;
    }

    private static void Validate(AlgorithmSettingsDto settings)
    {
        if (settings.SegmentPointCount is < 2 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(settings), "每段点数必须在 2 到 1000 之间。");
        if (settings.AdvanceAfterPoints < 1 || settings.AdvanceAfterPoints >= settings.SegmentPointCount)
            throw new ArgumentOutOfRangeException(nameof(settings), "续发间隔必须大于等于 1，且小于每段点数，以确保续发时保留当前位置重叠点。");
    }

    private static VehicleRoutePoint ToPoint(RcsMapNode node) => new()
        { PointCode = node.PointCode, X = node.X, Y = node.Y, Z = node.Z, Floor = node.Floor };
}
