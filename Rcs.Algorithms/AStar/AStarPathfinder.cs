using Rcs.Contracts.Map;

namespace Rcs.Algorithms.AStar;

/// <summary>纯矩阵 A* 实现：不依赖数据库、HTTP、SignalR 或 WCS。</summary>
public sealed class AStarPathfinder : IAStarPathfinder
{
    private static readonly (int X, int Y)[] Directions =
    [
        (0, -1), (1, 0), (0, 1), (-1, 0)
    ];

    public RouteDto FindPath(GridMapDto map, GridPoint start, GridPoint end)
    {
        if (!Inside(map, start) || !Inside(map, end))
            return new RouteDto { Message = "起点或终点超出地图范围。" };

        var obstacles = map.Obstacles.ToHashSet();
        obstacles.Remove(start);
        obstacles.Remove(end);

        var open = new PriorityQueue<GridPoint, int>();
        var cameFrom = new Dictionary<GridPoint, GridPoint>();
        var cost = new Dictionary<GridPoint, int> { [start] = 0 };
        open.Enqueue(start, Heuristic(start, end));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == end)
                return new RouteDto { Found = true, Points = Rebuild(cameFrom, current) };

            foreach (var direction in Directions)
            {
                var next = new GridPoint(current.X + direction.X, current.Y + direction.Y);
                if (!Inside(map, next) || obstacles.Contains(next)) continue;

                var nextCost = cost[current] + 1;
                if (cost.TryGetValue(next, out var oldCost) && nextCost >= oldCost) continue;

                cost[next] = nextCost;
                cameFrom[next] = current;
                open.Enqueue(next, nextCost + Heuristic(next, end));
            }
        }

        return new RouteDto { Message = "起点和终点之间没有可用路径。" };
    }

    private static bool Inside(GridMapDto map, GridPoint point) =>
        point.X >= 0 && point.X < map.Width && point.Y >= 0 && point.Y < map.Height;

    private static int Heuristic(GridPoint a, GridPoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static IReadOnlyList<GridPoint> Rebuild(Dictionary<GridPoint, GridPoint> cameFrom, GridPoint current)
    {
        var result = new List<GridPoint> { current };
        while (cameFrom.TryGetValue(current, out var previous))
        {
            current = previous;
            result.Add(current);
        }
        result.Reverse();
        return result;
    }
}
