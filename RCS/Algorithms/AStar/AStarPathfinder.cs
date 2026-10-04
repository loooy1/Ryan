using Contracts.Rcs.Map;
using Contracts.Rcs.Route;

namespace Rcs.Algorithms.AStar;

public sealed class AStarPathfinder : IAStarPathfinder
{
    public RouteDto FindPath(RcsMapSnapshot map, string startPointCode, string endPointCode)
        => FindPath(map, startPointCode, endPointCode,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public RouteDto FindPath(RcsMapSnapshot map, string startPointCode, string endPointCode,
        IReadOnlySet<string> blockedPoints, IReadOnlySet<string> blockedLines)
    {
        if (string.IsNullOrWhiteSpace(startPointCode) || string.IsNullOrWhiteSpace(endPointCode))
            return new RouteDto { Message = "起点和终点不能为空。" };
        if (!map.TryGetPoint(startPointCode, out var start) || !map.TryGetPoint(endPointCode, out var end))
            return new RouteDto { Message = "起点或终点不在当前已加载地图中。" };
        if (string.Equals(startPointCode, endPointCode, StringComparison.OrdinalIgnoreCase))
            return new RouteDto { Found = true, PointCodes = new[] { start.PointCode } };

        var open = new PriorityQueue<(string PointCode, double Cost), double>();
        var cameFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [start.PointCode] = 0 };
        open.Enqueue((start.PointCode, 0), Heuristic(start, end));

        while (open.TryDequeue(out var current, out _))
        {
            var currentCode = current.PointCode;
            // A better route may have enqueued this point again. Ignore the older queue entry.
            if (!cost.TryGetValue(currentCode, out var bestCost) || current.Cost > bestCost) continue;
            if (string.Equals(currentCode, end.PointCode, StringComparison.OrdinalIgnoreCase))
                return new RouteDto { Found = true, PointCodes = Rebuild(cameFrom, currentCode) };
            if (!map.Adjacency.TryGetValue(currentCode, out var edges)) continue;
            foreach (var edge in edges)
            {
                if (blockedPoints.Contains(edge.ToPointCode) || blockedLines.Contains(edge.LineCode)) continue;
                if (!map.Points.TryGetValue(edge.ToPointCode, out var next)) continue;
                var edgeCost = edge.Distance > 0 ? edge.Distance : Distance(map.Points[currentCode], next);
                var nextCost = current.Cost + edgeCost;
                if (cost.TryGetValue(next.PointCode, out var known) && nextCost >= known) continue;
                cost[next.PointCode] = nextCost;
                cameFrom[next.PointCode] = currentCode;
                open.Enqueue((next.PointCode, nextCost), nextCost + Heuristic(next, end));
            }
        }
        return new RouteDto { Message = "起点和终点之间没有可用路径。" };
    }

    private static double Heuristic(RcsMapNode a, RcsMapNode b) => Distance(a, b);
    private static double Distance(RcsMapNode a, RcsMapNode b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    private static IReadOnlyList<string> Rebuild(Dictionary<string, string> cameFrom, string current)
    {
        var result = new List<string> { current };
        while (cameFrom.TryGetValue(current, out var previous)) { current = previous; result.Add(current); }
        result.Reverse();
        return result;
    }
}
