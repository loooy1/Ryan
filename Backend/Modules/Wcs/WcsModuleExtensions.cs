using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Proxy.Services;

namespace WCSBackend.Modules.Wcs;

/// <summary>
/// Wcs 总模块的依赖注入注册（Wcs 为总目录，下面分 Automation / Proxy / Console / Realtime 子模块）。
/// 注册顺序注意：HostedService 需要以单例方式同时被控制器注入与宿主启动。
/// </summary>
public static class WcsModuleExtensions
{
    public static IServiceCollection AddWcsModule(this IServiceCollection services)
    {
        WcsMapping.Build();   // DTO ↔ 实体全量映射注册（Mapster，启动一次）
        services.AddHttpClient();   // IHttpClientFactory（GrcsHttpClient 用）

        // ── Automation（自动下发/信号/台账/日志/数据基础设施）──
        services.AddSingleton<AutomationLogService>();
        services.AddSingleton<MapStoreService>();
        services.AddSingleton<RangeConfigService>();
        services.AddSingleton<WcsSettingsService>();
        services.AddSingleton<CargoCodeStore>();
        services.AddSingleton<SignalConfirmStore>();
        services.AddSingleton<ExceptionRecordStore>();
        services.AddSingleton<ProjectLogStore>();
        services.AddSingleton<TaskTemplateStore>();
        services.AddSingleton<FeatureModuleStore>();
        services.AddSingleton<AutoTemplateStore>();
        services.AddSingleton<MockRuleStore>();
        services.AddSingleton<MockApprovalService>();
        services.AddSingleton<GrcsHttpClient>();
        // WCS 自持库存账本（自动化选池/占用/释放唯一事实源，SQLite 持久化）
        services.AddSingleton<WcsInventoryStore>();
        // GRCS 库存查询缓存（按需查询；自动化选池已用 WcsInventoryStore 账本，不再后台轮询）
        services.AddSingleton<GrcsInventoryCacheService>();
        services.AddSingleton<ManualInventoryService>();
        // 轮询/批量互斥闸（多标签页也能保证只有一个在执行）
        services.AddSingleton<AutomationGate>();
        // 纯移动任务循环（后端执行：选点/下发/统计/日志，SignalR 广播 MoveTaskStats）
        services.AddSingleton<MoveLoopRunner>();
        // 归巢模式（一次性批量下发：查车/选点/指定车 MOVE_ONLY，SignalR 广播 NestStats）
        services.AddSingleton<NestConfigService>();
        services.AddSingleton<NestRunner>();

        // 模块执行记录（内存环形缓冲，供「模块执行记录」面板增量拉取）
        // 统一模块执行引擎：起点/起点之后在下发时、终点在 FINISHED 后，统一在后端执行
        services.AddSingleton<TaskLifecycleService>();
        services.AddSingleton<ModuleEffectService>();
        services.AddSingleton<ModuleRunService>();
        // 任务完成协调器：统一监管 LOAD_FINISH/FINISHED 后的库存、锁与终点模块副作用
        services.AddSingleton<TaskCompletionCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<TaskCompletionCoordinator>());

        // 自动化模板执行引擎：单例 + IHostedService 双注册（控制器可注入操纵）
        services.AddSingleton<AutoTemplateRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoTemplateRunner>());
        // 信号自动放行：宿主启动即常驻（后端唯一，取代前端 leader 模式）

        // ── Console（控制台/阶段/台账/地图）──
        // 任务阶段事件跨请求共享（GRCS 上报 + 前端轮询），用 Singleton
        services.AddSingleton<ITaskStageService, TaskStageService>();

        return services;
    }
}
