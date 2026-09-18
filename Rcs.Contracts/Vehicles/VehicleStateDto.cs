using Rcs.Contracts.Map;

namespace Rcs.Contracts.Vehicles;

public sealed class VehicleStateDto
{
    public string Id { get; init; } = "V-01";
    public GridPoint Position { get; init; }
    public string Status { get; init; } = "Idle";
    public int RouteIndex { get; init; }
    public int RouteLength { get; init; }
}
