using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Proxy.Services;

namespace WCSBackend.Modules.Wcs;

/// <summary>
/// Wcs 鎬绘ā鍧楃殑渚濊禆娉ㄥ叆娉ㄥ唽锛圵cs 涓烘€荤洰褰曪紝涓嬮潰鍒?Automation / Proxy / Console / Realtime 瀛愭ā鍧楋級銆?
/// 娉ㄥ唽椤哄簭娉ㄦ剰锛欻ostedService 闇€瑕佷互鍗曚緥鏂瑰紡鍚屾椂琚帶鍒跺櫒娉ㄥ叆涓庡涓诲惎鍔ㄣ€?
/// </summary>
public static class WcsModuleExtensions
{
    public static IServiceCollection AddWcsModule(this IServiceCollection services)
    {
        WcsMapping.Build();   // DTO 鈫?瀹炰綋鍏ㄩ噺鏄犲皠娉ㄥ唽锛圡apster锛屽惎鍔ㄤ竴娆★級
        services.AddHttpClient();   // IHttpClientFactory锛圙rcsHttpClient 鐢級

        // 鈹€鈹€ Automation锛堣嚜鍔ㄤ笅鍙?淇″彿/鍙拌处/鏃ュ織/鏁版嵁鍩虹璁炬柦锛夆攢鈹€
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
        services.AddSingleton<ModuleExecutionLogStore>();
        services.AddSingleton<GrcsHttpClient>();
        // WCS 鑷寔搴撳瓨璐︽湰锛堣嚜鍔ㄥ寲閫夋睜/鍗犵敤/閲婃斁鍞竴浜嬪疄婧愶紝SQLite 鎸佷箙鍖栵級
        services.AddSingleton<WcsInventoryStore>();
        // GRCS 搴撳瓨鏌ヨ缂撳瓨锛堟寜闇€鏌ヨ锛涜嚜鍔ㄥ寲閫夋睜宸茬敤 WcsInventoryStore 璐︽湰锛屼笉鍐嶅悗鍙拌疆璇級
        services.AddSingleton<GrcsInventoryCacheService>();
        services.AddSingleton<ManualInventoryService>();
        // 杞/鎵归噺浜掓枼闂革紙澶氭爣绛鹃〉涔熻兘淇濊瘉鍙湁涓€涓湪鎵ц锛?
        services.AddSingleton<AutomationGate>();
        // 绾Щ鍔ㄤ换鍔″惊鐜紙鍚庣鎵ц锛氶€夌偣/涓嬪彂/缁熻/鏃ュ織锛孲ignalR 骞挎挱 MoveTaskStats锛?
        services.AddSingleton<MoveLoopRunner>();
        // 褰掑发妯″紡锛堜竴娆℃€ф壒閲忎笅鍙戯細鏌ヨ溅/閫夌偣/鎸囧畾杞?MOVE_ONLY锛孲ignalR 骞挎挱 NestStats锛?
        services.AddSingleton<NestConfigService>();
        services.AddSingleton<NestRunner>();

        // 妯″潡鎵ц璁板綍锛堝唴瀛樼幆褰㈢紦鍐诧紝渚涖€屾ā鍧楁墽琛岃褰曘€嶉潰鏉垮閲忔媺鍙栵級
        // 缁熶竴妯″潡鎵ц寮曟搸锛氳捣鐐?璧风偣涔嬪悗鍦ㄤ笅鍙戞椂銆佺粓鐐瑰湪 FINISHED 鍚庯紝缁熶竴鍦ㄥ悗绔墽琛?
        services.AddSingleton<TaskLifecycleService>();
        services.AddSingleton<ModuleEffectService>();
        services.AddSingleton<ModuleRunService>();
        // 浠诲姟瀹屾垚鍗忚皟鍣細缁熶竴鐩戠 LOAD_FINISH/FINISHED 鍚庣殑搴撳瓨銆侀攣涓庣粓鐐规ā鍧楀壇浣滅敤
        services.AddSingleton<TaskCompletionCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<TaskCompletionCoordinator>());

        // 鑷姩鍖栨ā鏉挎墽琛屽紩鎿庯細鍗曚緥 + IHostedService 鍙屾敞鍐岋紙鎺у埗鍣ㄥ彲娉ㄥ叆鎿嶇旱锛?
        services.AddSingleton<AutoTemplateRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoTemplateRunner>());
        // 淇″彿鑷姩鏀捐锛氬涓诲惎鍔ㄥ嵆甯搁┗锛堝悗绔敮涓€锛屽彇浠ｅ墠绔?leader 妯″紡锛?

        // 鈹€鈹€ Console锛堟帶鍒跺彴/闃舵/鍙拌处/鍦板浘锛夆攢鈹€
        // 浠诲姟闃舵浜嬩欢璺ㄨ姹傚叡浜紙GRCS 涓婃姤 + 鍓嶇杞锛夛紝鐢?Singleton
        services.AddSingleton<ITaskStageService, TaskStageService>();

        return services;
    }
}
