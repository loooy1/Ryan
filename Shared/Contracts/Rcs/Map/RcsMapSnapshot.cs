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
    public bool TryGetPoint(string pointCode, out RcsMapNode point)
    {
        var separator = pointCode.LastIndexOf('_');
        if (separator > 0 && int.TryParse(pointCode.AsSpan(separator + 1),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var floor)
            && Points.TryGetValue(pointCode[..separator], out point!))
        {
            if (Math.Abs(point.Z - floor) < 0.000001) return true;
            point = null!;
            return false;
        }
        if (Points.TryGetValue(pointCode, out point!)) return true;
        point = null!;
        return false;
    }
}

public sealed record RcsMapNode(string PointCode, string PointType, double X, double Y, double Z);
public sealed record RcsMapEdge(string LineCode, string FromPointCode, string ToPointCode, double Distance, string Direction, double MaxSpeed);
