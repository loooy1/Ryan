using Rcs.Algorithms.AStar;
using Contracts.Rcs.Map;
using Contracts.Rcs.Route;
using Contracts.Rcs.Vehicle;
using RCSBackend.Modules.Rcs.Application.Execution;
using RCSBackend.Modules.Rcs.Application.Maps;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;
using RCSBackend.Modules.Rcs.Application.Tasks;
using Contracts.Rcs.Tasks;

namespace RCSBackend.Modules.Rcs.Application.Simulation;

public sealed class RcsSimulationService
{
    private readonly IAStarPathfinder _pathfinder;
    private readonly RcsMapCache _mapCache;
    private readonly IRcsTaskExecutionService _executor;
    private readonly ILogger<RcsSimulationService> _logger;
    private readonly IRcsTaskService _tasks;
    public RcsSimulationService(IAStarPathfinder pathfinder, RcsMapCache mapCache, IRcsTaskExecutionService executor,
        IRcsTaskService tasks, ILogger<RcsSimulationService> logger)
    {
        _pathfinder = pathfinder;
        _mapCache = mapCache;
        _logger = logger;
        _executor = executor;
        _tasks = tasks;
    }

    public RcsMapSnapshot? Map => _mapCache.Current;
    public IReadOnlyList<VehicleStateDto> Vehicles => _executor.GetVehicles();
    public Task<RcsMapSnapshot?> ReloadMapAsync(CancellationToken cancellationToken = default) => _mapCache.ReloadAsync(cancellationToken);

    public RouteDto Preview(string startPointCode, string endPointCode)
    {
        var map = _mapCache.Current;
        if (map is null) return new RouteDto { Message = "没有已加载的地图，请先发布并设为当前地图。" };
        var route = _pathfinder.FindPath(map, startPointCode, endPointCode);
        _logger.LogInformation("路径预览完成 Start={Start} End={End} Found={Found} Nodes={Nodes}",
            startPointCode, endPointCode, route.Found, route.PointCodes.Count);
        return route;
    }

    public async Task<RouteDto> RunAsync(string startPointCode, string endPointCode, string vehicleId = "V-01", CancellationToken token = default)
    {
        var route = Preview(startPointCode, endPointCode);
        var map = _mapCache.Current;
        if (!route.Found || map is null)
        {
            _logger.LogWarning("虚拟车未启动：路径不可用 Start={Start} End={End} Reason={Reason}", startPointCode, endPointCode, route.Message);
            return route;
        }
        var id = "SimMove_" + Guid.NewGuid().ToString("N");
        await _tasks.ReceiveManualAsync(new RcsTaskReceiveRequest
        {
            GroupId = id, MsgTime = RcsTaskService.ProtocolTime(), Warehouse = map.SceneName,
            PriorityCode = 1,
            Tasks = [new RcsTaskRequest { TaskId = id, TaskType = "MOVE_ONLY", VehicleId = vehicleId, StationCode = [startPointCode, endPointCode] }]
        }, token);
        _logger.LogInformation("地图移动任务已入队 TaskId={TaskId} Start={Start} End={End}", id, startPointCode, endPointCode);
        return route;
    }
}
