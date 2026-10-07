using Contracts.Rcs.Protocol;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>算法路径转换后的车辆协议点，由 RCS 负责下发与追踪窗口。</summary>
public sealed record RcsVehicleRoutePlan
{
    public IReadOnlyList<VehicleRoutePoint> TotalPath { get; init; } = [];
    public IReadOnlyList<RcsVehicleRouteSegment> Segments { get; init; } = [];
}

public sealed record RcsVehicleRouteSegment
{
    public int StartPointOffset { get; init; }
    public IReadOnlyList<VehicleRoutePoint> Points { get; init; } = [];
}
