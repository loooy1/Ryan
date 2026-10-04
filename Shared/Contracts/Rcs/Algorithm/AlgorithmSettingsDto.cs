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
    public IReadOnlyList<Protocol.VehicleRoutePoint> Points { get; init; } = Array.Empty<Protocol.VehicleRoutePoint>();
}

/// <summary>Complete algorithm result: full path for RCS visibility and precomputed segments for vehicle delivery.</summary>
public sealed record AlgorithmRoutePlanDto
{
    public IReadOnlyList<Protocol.VehicleRoutePoint> TotalPath { get; init; } = Array.Empty<Protocol.VehicleRoutePoint>();
    public IReadOnlyList<RouteSegmentDto> Segments { get; init; } = Array.Empty<RouteSegmentDto>();
}

/// <summary>目标站点只包含位置；业务动作由 RCS 保留并在协议下发时附加。</summary>
public sealed record AlgorithmRouteStopDto(string PointCode);
