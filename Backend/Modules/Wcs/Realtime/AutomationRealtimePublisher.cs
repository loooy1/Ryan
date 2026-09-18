using System.Text.Json;
using Contracts.Dtos;
using Contracts.Entities;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Infrastructure;

namespace WCSBackend.Modules.Wcs.Realtime;

/// <summary>把自动化状态、日志和信号确认从服务端主动推送给前端，替代浏览器每秒轮询。</summary>
public sealed class AutomationRealtimePublisher : BackgroundService
{
    private readonly IHubContext<TaskStageRealtimeHub> _hub;
    private readonly AutoTemplateRunner _auto;
    private readonly AutoTemplateStore _templates;
    private readonly MoveLoopRunner _move;
    private readonly NestRunner _nest;
    private readonly WcsSettingsService _settings;
    private readonly AutomationLogService _logs;
    private readonly SignalConfirmStore _signals;
    private string _lastStatus = "";
    private string _lastLogs = "";
    private string _lastSignals = "";
    public AutoStatusSnapshot CurrentStatus { get; private set; } = new();
    public List<LogRoundDto> CurrentLogs { get; private set; } = [];
    public Dictionary<string, List<WorkflowStateRow>> CurrentSignals { get; private set; } = [];

    public AutomationRealtimePublisher(IHubContext<TaskStageRealtimeHub> hub, AutoTemplateRunner auto,
        AutoTemplateStore templates, MoveLoopRunner move, NestRunner nest, WcsSettingsService settings,
        AutomationLogService logs, SignalConfirmStore signals)
    {
        _hub = hub; _auto = auto; _templates = templates; _move = move; _nest = nest;
        _settings = settings; _logs = logs; _signals = signals;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _auto.Snapshot();
                var status = new AutoStatusSnapshot
                {
                    Running = snapshot.Running, AutoTabId = snapshot.ActiveTabId ?? "", Interval = snapshot.Interval,
                    ActiveTemplateId = snapshot.ActiveTemplateId ?? "", ActiveTemplateName = snapshot.ActiveTemplateName ?? "",
                    Executed = snapshot.Executed, Status = snapshot.Status, MoveRunning = _move.Running,
                    MoveTabId = _move.TabId ?? "", MoveTotal = _move.Total, MoveOk = _move.Ok, MoveFail = _move.Fail,
                    MoveLastError = _move.LastError ?? "", NestRunning = _nest.Running,
                    Templates = _templates.GetAll(), Settings = _settings.Get()
                };
                CurrentStatus = status;
                var statusJson = JsonSerializer.Serialize(status);
                if (statusJson != _lastStatus) { _lastStatus = statusJson; await _hub.Clients.All.SendAsync("AutomationStatus", status, stoppingToken); }

                var logs = _logs.GetRounds();
                CurrentLogs = logs;
                var logsJson = JsonSerializer.Serialize(logs);
                if (logsJson != _lastLogs) { _lastLogs = logsJson; await _hub.Clients.All.SendAsync("AutomationLogs", logs, stoppingToken); }

                var signals = _signals.GetAll();
                CurrentSignals = signals;
                var signalsJson = JsonSerializer.Serialize(signals);
                if (signalsJson != _lastSignals) { _lastSignals = signalsJson; await _hub.Clients.All.SendAsync("SignalConfirmState", signals, stoppingToken); }
            }
            catch { }
            try { await Task.Delay(1000, stoppingToken); } catch { break; }
        }
    }
}
