namespace Contracts.Rcs.Algorithm;

/// <summary>Algorithm-level route streaming configuration.</summary>
public sealed record AlgorithmSettingsDto
{
    public int SegmentPointCount { get; init; } = 4;
    public int AdvanceAfterPoints { get; init; } = 1;
}

/// <summary>A rolling route segment. StartPointOffset is zero-based in TotalPath.</summary>
public sealed record RouteSegmentDto
{
    public int StartPointOffset { get; init; }
    public IReadOnlyList<AlgorithmPathPointDto> Points { get; init; } = Array.Empty<AlgorithmPathPointDto>();
}

/// <summary>算法输出的完整位置路径和滚动分段；RCS 再转换为车辆协议点。</summary>
public sealed record AlgorithmRoutePlanDto
{
    public IReadOnlyList<AlgorithmPathPointDto> TotalPath { get; init; } = Array.Empty<AlgorithmPathPointDto>();
    public IReadOnlyList<RouteSegmentDto> Segments { get; init; } = Array.Empty<RouteSegmentDto>();
}

/// <summary>算法只输出地图位置；车辆动作和协议报文由 RCS 应用层转换。</summary>
public sealed record AlgorithmPathPointDto(string PointCode, double X, double Y, double Z);

/// <summary>目标站点只包含位置；业务动作由 RCS 保留并在协议下发时附加。</summary>
public sealed record AlgorithmRouteStopDto(string PointCode);
