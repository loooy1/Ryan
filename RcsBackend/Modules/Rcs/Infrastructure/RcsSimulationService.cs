using Rcs.Algorithms.AStar;
using Rcs.Contracts.Map;
using Rcs.VirtualVehicle;

namespace RCSBackend.Modules.Rcs.Infrastructure;

public sealed class RcsSimulationService
{
    private readonly IAStarPathfinder _pathfinder;
    private readonly IVirtualVehicle _vehicle;

    public RcsSimulationService(IAStarPathfinder pathfinder)
    {
        _pathfinder = pathfinder;
        _vehicle = new VirtualVehicleSimulator(new GridPoint(1, 1));
        Map = CreateDemoMap();
    }

    public GridMapDto Map { get; }
    public IVirtualVehicle Vehicle => _vehicle;

    public RouteDto Preview(GridPoint start, GridPoint end) => _pathfinder.FindPath(Map, start, end);

    public async Task<RouteDto> RunAsync(GridPoint start, GridPoint end)
    {
        var route = Preview(start, end);
        if (!route.Found) return route;
        _ = _vehicle.RunAsync(route.Points);
        await Task.CompletedTask;
        return route;
    }

    private static GridMapDto CreateDemoMap()
    {
        var obstacles = new List<GridPoint>();
        for (var x = 4; x <= 14; x++) obstacles.Add(new GridPoint(x, 5));
        for (var y = 2; y <= 9; y++) obstacles.Add(new GridPoint(9, y));
        obstacles.Remove(new GridPoint(9, 7));
        return new GridMapDto { Width = 18, Height = 12, Obstacles = obstacles };
    }
}
