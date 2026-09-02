using GRCS.Dashboard.Modules.WcsSimulator.Services;

namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 信号自动放行遥控壳（Skill E：自动确认逻辑已下沉到 GrcsBackend SignalAutoHostedService，
/// leader 模式整个删除——后端天然唯一，跨标签页一致）。
/// 本类只做开关遥控与状态展示（POST /api/wcs/auto/signals）。
/// </summary>
public class SignalAutoService
{
    private readonly WcsApiClient _api;
    private readonly AutomationHub _hub;

    public SignalAutoService(WcsApiClient api, AutomationHub hub)
    {
        _api = api;
        _hub = hub;
        _hub.Changed += OnHubChanged;
    }

    public bool ArrivalAuto => _hub.Status.Signals.ArrivalAuto;
    public bool RemovalAuto => _hub.Status.Signals.RemovalAuto;
    public bool AutoSend => _hub.Status.Signals.AutoSend;
    public bool Running => true;    // 后端常驻

    public event Action? Changed;
    private void OnHubChanged() => Changed?.Invoke();

    public Task ToggleArrivalAsync() => SetAsync(!ArrivalAuto, RemovalAuto, AutoSend);
    public Task ToggleRemovalAsync() => SetAsync(ArrivalAuto, !RemovalAuto, AutoSend);
    public Task ToggleSortingAsync() => SetAsync(ArrivalAuto, RemovalAuto, !AutoSend);

    /// <summary>乐观翻转快照（UI 立即响应，不等 1s 轮询），再 POST 到后端；POST 失败下轮轮询自动回滚。</summary>
    private async Task SetAsync(bool arrival, bool removal, bool sorting)
    {
        _hub.ApplySignals(arrival, removal, sorting);
        await _api.PostAsync("/api/wcs/auto/signals", new { arrivalAuto = arrival, removalAuto = removal, autoSend = sorting });
    }
}
