using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Automation.Services;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Proxy.Services;

namespace GrcsBackend.Modules.Wcs;

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
        services.AddSingleton<StationLockStore>();
        services.AddSingleton<LedgerStore>();
        services.AddSingleton<SignalConfirmStore>();
        services.AddSingleton<ExceptionRecordStore>();
        services.AddSingleton<ProjectLogStore>();
        services.AddSingleton<TaskTemplateStore>();
        services.AddSingleton<FeatureModuleStore>();
        services.AddSingleton<AutoTemplateStore>();
        services.AddSingleton<MockRuleStore>();
        services.AddSingleton<MockApprovalService>();
        services.AddSingleton<GrcsHttpClient>();
        // GRCS 库存后台轮询缓存（2 秒刷新，自动化选点/库存统计共用；任务完成后可即时强制刷新）
        services.AddSingleton<GrcsInventoryCacheService>();
        services.AddHostedService(sp => sp.GetRequiredService<GrcsInventoryCacheService>());
        // 轮询/批量互斥闸（多标签页也能保证只有一个在执行）
        services.AddSingleton<AutomationGate>();
        // 纯移动任务循环（后端执行：选点/下发/统计/日志，SignalR 广播 MoveTaskStats）
        services.AddSingleton<MoveLoopRunner>();
        // 归巢模式（一次性批量下发：查车/选点/指定车 MOVE_ONLY，SignalR 广播 NestStats）
        services.AddSingleton<NestConfigService>();
        services.AddSingleton<NestRunner>();

        // 模块执行记录（内存环形缓冲，供「模块执行记录」面板增量拉取）
        services.AddSingleton<ModuleExecLogStore>();
        // 统一模块执行引擎：起点/起点之后在下发时、终点在 FINISHED 后，统一在后端执行
        services.AddSingleton<ModuleRunService>();
        // 终点模块后台执行器：订阅 TaskFinished，对非 Auto_ 任务跑终点模块（自动化任务由 AutoTemplateRunner 自行跑）
        services.AddHostedService<FinishedModuleWatcher>();

        // 自动化模板执行引擎：单例 + IHostedService 双注册（控制器可注入操纵）
        services.AddSingleton<AutoTemplateRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoTemplateRunner>());
        // 信号自动放行：宿主启动即常驻（后端唯一，取代前端 leader 模式）
        services.AddSingleton<SignalAutoHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SignalAutoHostedService>());

        // ── Console（控制台/阶段/台账/地图）──
        // 任务阶段事件跨请求共享（GRCS 上报 + 前端轮询），用 Singleton
        services.AddSingleton<ITaskStageService, TaskStageService>();

        return services;
    }
}