using Contracts.Rcs.Map;
using Contracts.Rcs.Route;
using Contracts.Rcs.Vehicle;

namespace Dashboard.Modules.RcsSimulator.Models;

public sealed class RcsMapState
{
    public GridMapDto Map { get; set; } = new() { Width = 18, Height = 12 };
    public RouteDto Route { get; set; } = new();
    public VehicleStateDto Vehicle { get; set; } = new();
}
