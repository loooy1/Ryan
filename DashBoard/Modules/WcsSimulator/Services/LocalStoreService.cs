using System.Text.Json;
using Microsoft.JSInterop;

namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// localStorage 内存缓存层（scoped = per-browser-tab singleton）。
///
/// ── 为什么需要它 ──
/// Blazor WASM 每次读 localStorage 都要跨 JS interop 边界（异步 + 序列化开销）。
/// 本服务在 App 启动时把常用小 key 一次性批量加载进内存，之后所有读操作同步返回
/// （0ms、0 次 JS 边界）；写操作双写：内存立即更新（同步可见）+ localStorage 异步持久化。
///
/// ── 边界 ──
/// 只预加载配置/开关/折叠状态等小 key；台账（grcs_task_ledger，上限 2000 条大 JSON）
/// 和地图缓存（grcs_map_stations）等大 key 由各自服务（TaskLedgerService 等）独立缓存，
/// 避免启动时一次性反序列化大对象。新增持久化 key 时想清楚它属于哪一类。
///
/// ── 跨标签页 ──
/// 本服务的缓存是标签页内快照；其他标签页的写入通过 storage 事件桥（index.html 的
/// grcsRegisterStorageListener）通知各页面按需重读对应 key。
/// </summary>
public class LocalStoreService
{
    private readonly Dictionary<string, string?> _cache = new();
    private bool _preloaded;

    /// <summary>一次性加载所有 key 到内存（启动时调用一次，约 10ms）。</summary>
    public async Task PreloadAsync(IJSRuntime js)
    {
        if (_preloaded) return;
        try
        {
            var keysJson = JsonSerializer.Serialize(AllKeys);
            var batch = await js.InvokeAsync<string>("grcsStoreLoadAll", keysJson);
            if (!string.IsNullOrEmpty(batch) && batch != "null")
            {
                var store = JsonSerializer.Deserialize<Dictionary<string, string?>>(batch);
                if (store != null)
                {
                    foreach (var kv in store)
                        _cache[kv.Key] = kv.Value;
                }
            }
        }
        catch { /* localStorage 不可用时忽略，后续 Get() 返回 null */ }
        _preloaded = true;
    }

    /// <summary>同步读取（0ms，无 JS 边界）。</summary>
    public string? Get(string key) =>
        _cache.TryGetValue(key, out var v) ? v : null;

    /// <summary>同步索引器。</summary>
    public string? this[string key] => Get(key);

    /// <summary>双写：内存立即更新 + localStorage 异步持久化。</summary>
    public async Task SetAsync(IJSRuntime js, string key, string value)
    {
        _cache[key] = value;
        try { await js.InvokeVoidAsync("grcsStoreSave", key, value); } catch { }
    }

    /// <summary>预加载的 key 列表（排除巨型 history/ledger key；服务数据已下沉后端）。</summary>
    private static readonly string[] AllKeys =
    [
        // 共享配置
        "grcs_warehouse", "grcs_wcs_url", "grcs_grcs_url",
        // 折叠状态
        "grcs_ts_collapsed", "grcs_td_collapsed", "grcs_mr_collapsed",
        "grcs_inv_collapsed", "grcs_auto_collapsed",
        // 表单/任务状态
        "grcs_td_state",
        // 任务类型模板 / 功能模块（注册表持久化，跨页共享；新标签页需预载才能同步读）
        "grcs_td_templates", "grcs_si_modules",
        // 地图缓存（过渡期只读，验收后删）
        "grcs_map_stations",
        // 归巢车队（「归巢车队」页勾选，自动化任务页归巢执行用）
        "grcs_nest_fleet",
        // 当前项目（异常记录 / 项目记录按项目隔离，记住切换）
        "grcs_er_project", "grcs_pl_project",
    ];
}
