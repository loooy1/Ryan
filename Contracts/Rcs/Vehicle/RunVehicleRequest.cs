using Contracts.Rcs.Map;

namespace Contracts.Rcs.Vehicle;

public sealed class RunVehicleRequest
{
    public GridPoint Start { get; init; }
    public GridPoint End { get; init; }
}
