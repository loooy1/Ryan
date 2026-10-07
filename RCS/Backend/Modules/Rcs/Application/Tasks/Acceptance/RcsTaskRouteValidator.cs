using Contracts.Rcs.Map;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

internal static class RcsTaskRouteValidator
{
    public static void Validate(RcsMapSnapshot map, string taskId, IReadOnlyList<string> stationCodes)
    {
        for (var i = 1; i < stationCodes.Count; i++)
        {
            var start = stationCodes[i - 1];
            var destination = stationCodes[i];
            if (HasStaticPath(map, start, destination)) continue;
            throw new ArgumentException($"任务 {taskId} 的站点路径不可达：{start} → {destination}。");
        }
    }

    private static bool HasStaticPath(RcsMapSnapshot map, string start, string destination)
    {
        if (string.Equals(start, destination, StringComparison.OrdinalIgnoreCase)) return true;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var current))
        {
            if (!map.Adjacency.TryGetValue(current, out var edges)) continue;
            foreach (var edge in edges)
            {
                if (string.Equals(edge.ToPointCode, destination, StringComparison.OrdinalIgnoreCase)) return true;
                if (visited.Add(edge.ToPointCode)) pending.Enqueue(edge.ToPointCode);
            }
        }
        return false;
    }
}
