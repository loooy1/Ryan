using Rcs.Contracts.Map;

namespace Rcs.Contracts.Vehicles;

public sealed class RunVehicleRequest
{
    public GridPoint Start { get; init; }
    public GridPoint End { get; init; }
}
