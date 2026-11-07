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
    private readonly BackendHealthService _health;

    public RuntimeConfigurationCoordinator(
        LocalStoreService store,
        IJSRuntime js,
        TaskStageHub stage,
        AutomationHub automation,
        BackendHealthService health)
    {
        _store = store;
        _js = js;
        _stage = stage;
        _automation = automation;
        _health = health;
    }

    /// <summary>保存运行时配置，并等待受影响的运行时服务切换到新配置。</summary>
    public async Task SaveAsync(string? wcsUrl = null, string? grcsUrl = null, string? warehouse = null, CancellationToken cancellationToken = default)
    {
        var wcsChanged = await SetIfChangedAsync(WcsEndpointKey, wcsUrl);
        var grcsChanged = await SetIfChangedAsync(GrcsEndpointKey, grcsUrl);
        await SetIfChangedAsync(WarehouseKey, warehouse);

        // 普通重复保存或仅修改场景名时，若当前连接健康，不要额外重连/探测，
        // 避免健康探测接口的偶发慢响应被误判成配置保存失败。
        var shouldVerifyConnections = wcsChanged || grcsChanged
            || _health.WcsOnline != true || _health.GrcsOnline != true;
        if (!shouldVerifyConnections) return;

        var refreshTasks = new List<Task>();
        if (wcsChanged || _health.WcsOnline != true)
            refreshTasks.Add(_stage.RefreshConnectionAsync(cancellationToken));

        var healthRefresh = _automation.RefreshConfigurationAsync(cancellationToken);
        refreshTasks.Add(healthRefresh);

        // 设置页只有在受影响服务已经读取新配置后才会收到完成信号。
        await Task.WhenAll(refreshTasks).WaitAsync(cancellationToken);
        if (!await healthRefresh)
            throw new InvalidOperationException("WCS 或 RCS 健康检查未通过。");
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
