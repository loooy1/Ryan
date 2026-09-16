using Contracts.Dtos;
using Contracts.Entities;

namespace Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 自动化状态/日志共享轮询中枢（Skill E：数据源在后端 WCSBackend）。
/// 每 1 秒拉一次 /api/wcs/auto/status 快照 + /api/wcs/auto/logs?sinceId 增量日志，
/// 以及进入申请 /api/wcs/status + /api/wcs/events（信号交互页进入信号多标签页同步）。
/// AutoRunService / ContainerTaskService / obsolete signal service 三个瘦壳共享同一份数据与 Changed 事件。
/// 同时兼任后端健康探测源：每轮把 WCS（/api/wcs/status）与 GRCS（/api/wcs/grcs/health 代理）
/// 状态回报给 BackendHealthService（BackendStatus 渲染 + 各页面连接判定，单一数据源）。
/// 常驻：由 MainLayout 注入启动，任何页面打开即轮询。
/// </summary>
public class AutomationHub : IDisposable
{
    private readonly WcsApiClient _api;
    private readonly BackendHealthService _health;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _lock = new();

    public AutoStatusSnapshot Status { get; private set; } = new();
    /// <summary>按轮次分组的日志（每轮一个标题，含该轮所有条目）。</summary>
    public List<LogRoundDto> Rounds { get; } = [];
    /// <summary>选点范围配置快照（随轮询刷新；AutomationTasks 页跨标签页同步用）。</summary>
    public RangeConfigDto Range { get; private set; } = new();

    // ── 信号确认状态（kind → 行；跨标签页同步，SignalInteraction 确认/已发送集合的事实源）──
    public Dictionary<string, List<WorkflowStateRow>> ConfirmState { get; private set; } = [];

    public event Action? Changed;

    public AutomationHub(WcsApiClient api, BackendHealthService health)
    {
        _api = api;
        _health = health;
        _loop = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(); }
            catch { }
            try { await Task.Delay(1000, ct); } catch { return; }
        }
    }

    /// <summary>立即拉一轮（手动操作后调用，避免等下一拍）。</summary>
    public async Task RefreshNowAsync()
    {
        // 不设置 SuppressAlerts，让手动操作的告警能弹出
        try { await PollOnceAsync(); }
        catch { }
    }

    /// <summary>乐观更新：信号自动开关（到达/移除）POST 后立即反映到快照，不等下一轮轮询。</summary>
    private async Task PollAsync()
    {
        _api.SuppressAlerts = true; // 自动轮询不弹告警，避免反复刷屏
        try { await PollOnceAsync(); }
        finally { _api.SuppressAlerts = false; } // 恢复手动操作的告警弹窗
    }

    /// <summary>执行一轮轮询（不修改 SuppressAlerts，由调用方控制）。</summary>
    private async Task PollOnceAsync()
    {
        var st = await _api.GetAsync<AutoStatusSnapshot>("/api/wcs/auto/status");
        if (st != null) Status = st;

        // 选点范围（自动化任务页「开启/关闭限制」等跨标签页同步）
        var range = await _api.GetAsync<RangeConfigDto>("/api/wcs/auto/range");
        if (range != null) Range = range;

        // 进入申请状态（用于后端健康判定；进入信号已由 MockApprovalService 取代）
        var adm = await _api.GetAsync<AdmittanceStatusDto>("/api/wcs/status");
        _health.ReportWcs(adm != null);

        // 信号确认状态（跨标签页同步，SignalInteraction 事实源）
        var wf = await _api.GetAsync<Dictionary<string, List<WorkflowStateRow>>>("/api/wcs/signal-confirm");
        if (wf != null) ConfirmState = wf;

        // 按轮次分组的日志（每轮一个标题；任务完成后后端清除该轮）
        var rounds = await _api.GetAsync<List<LogRoundDto>>("/api/wcs/auto/logs");
        if (rounds != null)
        {
            lock (_lock)
            {
                Rounds.Clear();
                Rounds.AddRange(rounds);
            }
        }

        // 健康探测：GRCS 经 WCS 代理轻量探测（后端 2s 短超时）→ 回报共享健康服务
        var grcs = await _api.GetAsync<GrcsProxyResult>("/api/wcs/grcs/health");
        _health.ReportGrcs(grcs?.Ok == true);
        Changed?.Invoke();
    }

    public void ClearLogs()
    {
        lock (_lock) { Rounds.Clear(); }
        _ = _api.DeleteAsync("/api/wcs/auto/logs");
    }

    public void Dispose() => _cts.Cancel();
}
