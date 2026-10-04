using Microsoft.Extensions.DependencyInjection;
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
using RCSBackend.Modules.Rcs.Protocol;
using RCSBackend.Modules.Rcs.Application.Vehicles;

namespace RCSBackend.Modules.Rcs;

/// <summary>
/// RCS 模块依赖注入：地图缓存、算法、任务接收与调度、虚拟车、数据库存储和实时推送。
/// Application/Tasks 管理任务；Scheduling 产生分配决策；Execution 规划路径并驱动车辆。
/// </summary>
public static class RcsModuleExtensions
{
    public static IServiceCollection AddRcsModule(this IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(RcsSimulationController).Assembly);
        services.AddSingleton<IAStarPathfinder, AStarPathfinder>();
        services.AddSingleton<IPathPlanningAlgorithm, AStarRoutePlanningAlgorithm>();
        services.AddSingleton<RcsDispatchLock>();
        services.AddSingleton<IRcsVehicleStore, RcsVehicleStore>();
        services.AddSingleton<RcsVehicleRegistry>();
        services.AddSingleton<RcsVehicleService>();
        services.AddSingleton<IVehicleCommandChannel, InProcessVehicleCommandChannel>();
        services.AddSingleton<RcsTaskRoutePlanner>();
        services.AddSingleton<RcsAlgorithmSettingsService>();
        services.AddSingleton<MultiVehicleTrafficCoordinator>();
        services.AddSingleton<IMultiVehicleTrafficCoordinator>(sp => sp.GetRequiredService<MultiVehicleTrafficCoordinator>());
        services.AddSingleton<IRcsTaskStore, RcsTaskStore>();
        services.AddSingleton<IRcsTaskScheduler, RcsTaskScheduler>();
        services.AddSingleton<IRcsTaskExecutionService, RcsTaskExecutionService>();
        services.AddSingleton<IRcsTaskService, RcsTaskService>();
        services.AddSingleton<RcsSimulationService>();
        services.AddSingleton<RcsMapStore>();
        services.AddSingleton<RcsMapCache>();
        services.AddSingleton<RcsRealtimePublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<RcsRealtimePublisher>());
        services.AddHostedService<RcsTaskDispatchWorker>();
        return services;
    }
}
