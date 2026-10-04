using Microsoft.JSInterop;

namespace Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 运行时配置协调器：集中处理配置保存后的缓存同步、连接重建和状态刷新。
/// 页面只提交配置，不直接依赖具体的 SignalR 或轮询服务。
/// </summary>
public sealed class RuntimeConfigurationCoordinator
{
    public const string WcsEndpointKey = "grcs_wcs_url";
    public const string GrcsEndpointKey = "grcs_grcs_url";
    public const string WarehouseKey = "grcs_warehouse";

    private readonly LocalStoreService _store;
    private readonly IJSRuntime _js;
    private readonly TaskStageHub _stage;
    private readonly AutomationHub _automation;

    public RuntimeConfigurationCoordinator(
        LocalStoreService store,
        IJSRuntime js,
        TaskStageHub stage,
        AutomationHub automation)
    {
        _store = store;
        _js = js;
        _stage = stage;
        _automation = automation;
    }

    /// <summary>保存运行时配置，并在后台刷新受影响服务；连接刷新不能阻塞配置保存。</summary>
    public async Task SaveAsync(string? wcsUrl = null, string? grcsUrl = null, string? warehouse = null)
    {
        var wcsChanged = await SetIfChangedAsync(WcsEndpointKey, wcsUrl);
        var grcsChanged = await SetIfChangedAsync(GrcsEndpointKey, grcsUrl);
        var warehouseChanged = await SetIfChangedAsync(WarehouseKey, warehouse);

        if (wcsChanged)
            _ = RefreshStageInBackgroundAsync();

        if (wcsChanged || grcsChanged || warehouseChanged)
            _ = RefreshAutomationInBackgroundAsync();
    }

    private async Task RefreshStageInBackgroundAsync()
    {
        try { await _stage.RefreshConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { /* 地址保存不应因旧连接断开或 SignalR 重连失败而失败 */ }
    }

    private async Task RefreshAutomationInBackgroundAsync()
    {
        try { await _automation.RefreshNowAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* 自动化状态会由常驻刷新循环继续更新 */ }
    }

    private async Task<bool> SetIfChangedAsync(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().TrimEnd('/');
        if (string.Equals(_store[key]?.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)) return false;
        await _store.SetAsync(_js, key, normalized);
        return true;
    }
}
