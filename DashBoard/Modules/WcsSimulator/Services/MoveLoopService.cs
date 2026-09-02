using GrcsBackend.Contracts.Dtos;
using GRCS.Dashboard.Modules.WcsSimulator.Services;

namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 纯移动任务循环遥控壳（scoped：跨页面导航存活）。
/// 循环本身已迁移到后端 MoveLoopRunner（选点/下发/统计/系统日志全在后端），
/// 本服务只负责：Start/Stop 通知后端，状态经 SignalR（TaskStageHub.MoveTaskStats）实时同步。
/// 离开「自动化任务」页任务继续下发（后端运行），返回后统计/状态自动恢复显示。
/// </summary>
public class MoveLoopService : IDisposable
{
    public class Options
    {
        public string TabId { get; set; } = "";
        public string OrderIdPrefix { get; set; } = "SimMoveOnly";
        public int Priority { get; set; } = 50;
        public int Interval { get; set; } = 3;
    }

    private readonly WcsApiClient _api;
    private readonly TaskStageHub _stage;

    public bool Running { get; private set; }
    public int Seq { get; private set; }
    public int Total { get; private set; }
    public int Ok { get; private set; }
    public int Fail { get; private set; }
    public string? LastError { get; private set; }
    public string? LastStation { get; private set; }
    public int Interval => _lastStats?.Interval ?? 3;
    private MoveTaskStatsDto? _lastStats;

    /// <summary>状态变化（SignalR 推送或本地乐观更新触发，页面订阅后 InvokeAsync(StateHasChanged)）。</summary>
    public event Action? Changed;

    public MoveLoopService(WcsApiClient api, TaskStageHub stage)
    {
        _api = api;
        _stage = stage;
        _stage.MoveStatsChanged += OnMoveStats;
        OnMoveStats();   // 若已收到过快照则立即同步一次
    }

    private void OnMoveStats()
    {
        var dto = _stage.MoveStats;
        if (dto == null) return;
        _lastStats = dto;
        Running = dto.Running;
        Seq = dto.Seq;
        Total = dto.Total;
        Ok = dto.Ok;
        Fail = dto.Fail;
        LastError = string.IsNullOrEmpty(dto.LastError) ? null : dto.LastError;
        LastStation = string.IsNullOrEmpty(dto.LastStation) ? null : dto.LastStation;
        Changed?.Invoke();
    }

    /// <summary>通知后端启动循环（互斥/参数校验由后端 AutomationGate + MoveLoopRunner 处理）。</summary>
    public async Task<(bool Ok, string? Reason)> StartAsync(Options opts)
    {
        var resp = await _api.PostAsync<object, MoveLeaseResult>("/api/wcs/auto/move/start", new
        {
            tabId = opts.TabId,
            interval = opts.Interval,
            priority = opts.Priority,
            orderIdPrefix = opts.OrderIdPrefix,
        });
        if (resp is not { Success: true })
        {
            LastError = resp?.Reason ?? "后端拒绝（互斥：自动化模板或另一标签页正在下发）";
            Changed?.Invoke();
            return (false, LastError);
        }
        Running = true;   // 乐观更新：后端随后广播真实快照校准
        LastError = null;
        Changed?.Invoke();
        return (true, null);
    }

    /// <summary>通知后端停止循环（取消循环、汇总日志、释放互斥）。</summary>
    public void Stop()
    {
        if (!Running) return;
        Running = false;   // 乐观更新：后端停止后广播快照校准
        _ = _api.PostAsync("/api/wcs/auto/move/stop", new { tabId = "" });
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stage.MoveStatsChanged -= OnMoveStats;
    }
}