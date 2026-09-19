using Rcs.Algorithms.AStar;
using Contracts.Rcs.Map;
using Contracts.Rcs.Route;
using Rcs.VirtualVehicle;

namespace RCSBackend.Modules.Rcs.Infrastructure;

public sealed class RcsSimulationService
{
    private readonly IAStarPathfinder _pathfinder;
    private readonly IVirtualVehicle _vehicle;
    private readonly ILogger<RcsSimulationService> _logger;

    public RcsSimulationService(IAStarPathfinder pathfinder, ILogger<RcsSimulationService> logger)
    {
        _pathfinder = pathfinder;
        _logger = logger;
        _vehicle = new VirtualVehicleSimulator(new GridPoint(1, 1));
        Map = CreateDemoMap();
    }

    public GridMapDto Map { get; }
    public IVirtualVehicle Vehicle => _vehicle;

    public RouteDto Preview(GridPoint start, GridPoint end)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["LogCategory"] = "Automation",
            ["Component"] = "RouteSimulation",
            ["EventName"] = "RoutePreview"
        });
        var route = _pathfinder.FindPath(Map, start, end);
        _logger.LogInformation("路径预览完成 Start={Start} End={End} Found={Found} Nodes={Nodes}",
            start, end, route.Found, route.Points.Count);
        return route;
    }

    public async Task<RouteDto> RunAsync(GridPoint start, GridPoint end)
    {
        var route = Preview(start, end);
        if (!route.Found)
        {
            _logger.LogWarning("虚拟车未启动：路径不可用 Start={Start} End={End} Reason={Reason}", start, end, route.Message);
            return route;
        }
        _ = _vehicle.RunAsync(route.Points);
        _logger.LogInformation("虚拟车开始执行路径 Start={Start} End={End} Nodes={Nodes}", start, end, route.Points.Count);
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
