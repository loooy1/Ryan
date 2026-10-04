namespace Contracts.Rcs.Map;

/// <summary>RCS 从数据库加载后的只读地图快照，算法只依赖此快照。</summary>
public sealed class RcsMapSnapshot
{
    public string MapCode { get; init; } = "";
    public string Name { get; init; } = "";
    public string SceneName { get; init; } = "";
    public int Version { get; init; }
    public IReadOnlyDictionary<string, RcsMapNode> Points { get; init; } =
        new Dictionary<string, RcsMapNode>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyList<RcsMapEdge>> Adjacency { get; init; } =
        new Dictionary<string, IReadOnlyList<RcsMapEdge>>(StringComparer.OrdinalIgnoreCase);
    public bool TryGetPoint(string pointCode, out RcsMapNode point) => Points.TryGetValue(pointCode, out point!);
}

public sealed record RcsMapNode(string PointCode, string PointName, string PointType, int Floor, double X, double Y, double Z);
public sealed record RcsMapEdge(string LineCode, string FromPointCode, string ToPointCode, double Distance, string Direction, double MaxSpeed);
