using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GRCS.Dashboard.Modules.WcsSimulator.Services;

namespace GRCS.Dashboard.Modules.WcsSimulator.Components;

/// <summary>
/// 模拟器页面基类：统一反馈提示（PageBase）+ 折叠状态持久化 + localStorage 读取辅助。
/// 折叠状态统一存 localStorage（键由子类 CollapsedStoreKey 指定），
/// 消除各页面重复的 _collapsed/IsCollapsed/Toggle/V() 样板代码。
/// </summary>
public class PageStateBase : PageBase
{
    [Inject] protected LocalStoreService LocalStore { get; set; } = null!;
    [Inject] protected IJSRuntime Js { get; set; } = null!;

    /// <summary>折叠状态持久化键（子类覆写；空 = 不持久化，仅内存）。</summary>
    protected virtual string CollapsedStoreKey => "";

    /// <summary>未记录过的键默认折叠与否（TaskDispatch 默认全部展开 = false）。</summary>
    protected virtual bool DefaultCollapsedValue => true;

    protected Dictionary<string, bool> _collapsed = [];

    protected bool IsCollapsed(string key) => _collapsed.GetValueOrDefault(key, DefaultCollapsedValue);

    /// <summary>翻转折叠状态并持久化（写失败静默，不影响交互）。</summary>
    protected async Task Toggle(string key)
    {
        _collapsed[key] = !IsCollapsed(key);
        if (string.IsNullOrEmpty(CollapsedStoreKey)) return;
        try { await LocalStore.SetAsync(Js, CollapsedStoreKey, JsonSerializer.Serialize(_collapsed)); } catch { }
    }

    /// <summary>恢复折叠状态（仅当保存过才覆盖；默认折叠行为由 DefaultCollapsedValue 决定）。</summary>
    protected void RestoreCollapsed()
    {
        if (V(CollapsedStoreKey) is string cjson && !string.IsNullOrEmpty(cjson))
            _collapsed = JsonSerializer.Deserialize<Dictionary<string, bool>>(cjson) ?? _collapsed;
    }

    /// <summary>localStorage 读取辅助：null / 字符串 "null" 统一归一为 null。</summary>
    protected string? V(string k) => LocalStore[k] is { } v && v != "null" ? v : null;
}