using Microsoft.Extensions.DependencyInjection;
using Rcs.Algorithms.AStar;
using RCSBackend.Modules.Rcs.Console;
using RCSBackend.Modules.Rcs.Infrastructure;
using RCSBackend.Modules.Rcs.Realtime;

namespace RCSBackend.Modules.Rcs;

/// <summary>
/// Rcs 总模块的依赖注入注册（Rcs 为总目录，下面分 Protocol / Console / Realtime / Infrastructure 子模块）。
/// 骨架阶段：仅注册空模块；业务定义后在此挂接：
/// - Protocol/   GRCS 协议服务端（/api/Cargo、/api/Map/GetMap、/api/RawOrder/ChangeFloor、/api/v1/task_receive 等）
/// - Console/    RCS 管理接口（/api/rcs/* 控制器）
/// - Realtime/   RCS 实时推送 Hub
/// - Infrastructure/ RCS 实体 + EF 配置（Entities/Configurations，随 Add-Migration 建表）
/// </summary>
public static class RcsModuleExtensions
{
    public static IServiceCollection AddRcsModule(this IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(RcsSimulationController).Assembly);
        services.AddSingleton<IAStarPathfinder, AStarPathfinder>();
        services.AddSingleton<RcsSimulationService>();
        services.AddSingleton<RcsRealtimePublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<RcsRealtimePublisher>());
        return services;
    }
}
