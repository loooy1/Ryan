using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Contracts.Rcs.Tasks;
using Rcs.Algorithms.AStar;
using Rcs.Algorithms;
using Rcs.Algorithms.Traffic;
using RCSBackend.Modules.Rcs.Api;
using RCSBackend.Modules.Rcs.Application.Simulation;
using RCSBackend.Modules.Rcs.Realtime;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;
using RCSBackend.Modules.Rcs.Application.Tasks;
using RCSBackend.Modules.Rcs.Application.Scheduling;
using RCSBackend.Modules.Rcs.Application.Execution;
using RCSBackend.Modules.Rcs.Application.Inventory;
using RCSBackend.Modules.Rcs.Application.Maps;
using RCSBackend.Modules.Rcs.Protocol;
using RCSBackend.Modules.Rcs.Application.Vehicles;
using RCSBackend.Modules.Rcs.Application.StationBusiness;

namespace RCSBackend.Modules.Rcs;

/// <summary>
/// RCS 模块依赖注入：地图缓存、算法、任务接收与调度、虚拟车、数据库存储和实时推送。
/// Application/Tasks 管理任务；Scheduling 产生分配决策；Execution 规划路径并驱动车辆。
/// </summary>
public static class RcsModuleExtensions
{
    public static IServiceCollection AddRcsModule(this IServiceCollection services)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(RcsSimulationController).Assembly)
            .ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    var details = context.ModelState.Values
                        .SelectMany(value => value.Errors)
                        .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "请求参数无效。" : error.ErrorMessage);
                    return new BadRequestObjectResult(RcsApiResponse.Rejected(string.Join("；", details)));
                };
            });
        services.AddSingleton<IAStarPathfinder, AStarPathfinder>();
        services.AddSingleton<IPathPlanningAlgorithm, AStarRoutePlanningAlgorithm>();
        services.AddSingleton<RcsDispatchLock>();
        services.AddSingleton<IRcsVehicleStore, RcsVehicleStore>();
        services.AddSingleton<RcsVehicleRegistry>();
        services.AddSingleton<RcsVehicleService>();
        services.AddSingleton<IVehicleCommandChannel, VehicleCommandChannel>();
        services.AddSingleton<IVehicleProtocolAdapter, VirtualVehicleProtocolAdapter>();
        services.AddSingleton<RcsTaskRoutePlanner>();
        services.AddSingleton<RcsVehicleTrafficStateSynchronizer>();
        services.AddSingleton<RcsAlgorithmSettingsService>();
        services.AddHttpClient("station-business");
        services.AddSingleton<RcsStationBusinessRuleService>();
        services.AddSingleton<RcsInventoryTransferService>();
        services.AddSingleton<RcsInventoryCatalogService>();
        services.AddSingleton<RcsTaskStationRuleCoordinator>();
        services.AddSingleton<RcsVehicleActionExecutor>();
        services.AddSingleton<RcsRouteWindowManager>();
        services.AddSingleton<MultiVehicleTrafficCoordinator>();
        services.AddSingleton<IMultiVehicleTrafficCoordinator>(sp => sp.GetRequiredService<MultiVehicleTrafficCoordinator>());
        services.AddSingleton<IRcsTaskStore, RcsTaskStore>();
        services.AddSingleton<IRcsTaskScheduler, RcsTaskScheduler>();
        services.AddSingleton<RcsTaskAcceptanceService>();
        services.AddSingleton<IRcsTaskExecutionService, RcsTaskExecutionService>();
        services.AddSingleton<RcsTaskService>();
        services.AddSingleton<IRcsTaskService>(sp => sp.GetRequiredService<RcsTaskService>());
        services.AddSingleton<IRcsTaskDispatchRuntime>(sp => sp.GetRequiredService<RcsTaskService>());
        services.AddSingleton<IRcsTaskQueryService, RcsTaskQueryService>();
        services.AddSingleton<RcsSimulationService>();
        services.AddSingleton<RcsMapStore>();
        services.AddSingleton<RcsMapCache>();
        services.AddSingleton<RcsMapPublicationService>();
        services.AddSingleton<RcsRealtimePublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<RcsRealtimePublisher>());
        services.AddHostedService<RcsVehicleHeartbeatMonitor>();
        services.AddHostedService<RcsTaskDispatchWorker>();
        return services;
    }
}
