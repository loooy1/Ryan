using System.Text.Json;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Proxy.Services;
using GrcsBackend.Modules.Wcs.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 纯移动任务循环（MOVE_ONLY）后端执行引擎。
/// 前端只负责「通知开启/关闭轮询」（POST move/start、move/stop），
/// 选点 / 组装任务 / 调 GRCS / 统计 / 系统日志全部在本服务内完成，
/// 每轮下发后经 SignalR（/hubs/task-stages）广播 MoveTaskStats 实时回推前端。
/// 固定节拍：每 Interval 秒整点下发一个（dispatch 慢不拖累节奏，落后不堆积）；
/// 与模板自动化经 AutomationGate 互斥，单实例串行，杜绝并发下发。
/// </summary>
public class MoveLoopRunner
{
    private readonly GrcsHttpClient _grcs;
    private readonly MapStoreService _map;
    private readonly RangeConfigService _range;
    private readonly WcsSettingsService _settings;
    private readonly AutomationLogService _logs;
    private readonly AutomationGate _gate;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    private readonly object _stateLock = new();
    private bool _running;
    private string? _tabId;
    private int _interval = 3;            // 秒
    private string _orderIdPrefix = "SimMoveOnly";
    private int _priority = 50;
    private int _seq;
    private int _total;
    private int _ok;
    private int _fail;
    private string? _lastError;
    private string? _lastStation;
    private CancellationTokenSource? _cts;

    public MoveLoopRunner(GrcsHttpClient grcs, MapStoreService map, RangeConfigService range,
        WcsSettingsService settings, AutomationLogService logs, AutomationGate gate,
        IHubContext<TaskStageRealtimeHub> hub)
    {
        _grcs = grcs;
        _map = map;
        _range = range;
        _settings = settings;
        _logs = logs;
        _gate = gate;
        _hub = hub;
    }

    // ── 状态（前端统计条 / 状态栏 / status 接口共用）──
    public bool Running => _running;
    public string? TabId => _tabId;
    public int Interval => _interval;
    public int Seq => _seq;
    public int Total => _total;
    public int Ok => _ok;
    public int Fail => _fail;
    public string? LastError => _lastError;
    public string? LastStation => _lastStation;

    public MoveTaskStatsDto Snapshot() => new()
    {
        Running = _running,
        TabId = _tabId,
        Interval = _interval,
        Seq = _seq,
        Total = _total,
        Ok = _ok,
        Fail = _fail,
        LastError = _lastError,
        LastStation = _lastStation,
    };

    /// <summary>登记互斥租约并启动固定节拍循环。失败（互斥/参数）返回原因。</summary>
    public (bool Ok, string? Reason) Start(StartMoveReq req)
    {
        if (string.IsNullOrWhiteSpace(req.TabId)) return (false, "缺少 tabId");
        var interval = Math.Clamp(req.Interval > 0 ? req.Interval : 3, 1, 600);
        var (ok, reason) = _gate.TryStartMove(req.TabId);
        if (!ok)
        {
            _logs.Add("❌ 纯移动任务循环被拒绝：" + reason, "#f87171");
            Broadcast();
            return (false, reason);
        }
        lock (_stateLock)
        {
            _running = true;
            _tabId = req.TabId;
            _interval = interval;
            _orderIdPrefix = string.IsNullOrWhiteSpace(req.OrderIdPrefix) ? "SimMoveOnly" : req.OrderIdPrefix.Trim();
            _priority = req.Priority > 0 ? req.Priority : 50;
            _seq = 0; _total = 0; _ok = 0; _fail = 0; _lastError = null; _lastStation = null;
        }
        var pool = BuildPool();
        _logs.Add($"▶ 纯移动任务循环开始：每 {_interval} 秒随机下发 1 个纯移动任务（候选池 {pool.Count} 个，选点范围{(_range.Get().Enabled ? "已开启" : "未开启=全图")}）", "#60a5fa");
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = DispatchLoopAsync(_cts.Token);
        Broadcast();
        return (true, null);
    }

    /// <summary>停止循环：取消循环 → 汇总日志 → 释放互斥 → 广播。</summary>
    public void Stop(string? tabId)
    {
        string? owner;
        lock (_stateLock)
        {
            if (!_running) return;
            if (!string.IsNullOrEmpty(tabId) && tabId != _tabId) return;   // 非属主忽略
            owner = _tabId;
            _running = false;
            _tabId = null;
        }
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _logs.Add($"⏹ 纯移动任务循环已停止（共下发 {_total} 个任务：成功 {_ok} / 失败 {_fail}）", "#fbbf24");
        lock (_stateLock) { _total = 0; _ok = 0; _fail = 0; _lastError = null; _lastStation = null; }
        _gate.StopMove(owner ?? "");
        Broadcast();
    }

    /// <summary>候选站点池：选点范围（类型/楼层/Mark 白名单）过滤全图站点；未开启范围 = 全图。</summary>
    private List<MapStationLite> BuildPool()
        => _range.Get().ApplyTo(_map.GetStations())
            .Where(s => s.StaEnable)
            .ToList();

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        // 固定节拍：每 interval 秒整点下发一次；dispatch 慢不拖累下一轮节奏，落后不堆积
        var nextTick = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            var pool = BuildPool();
            if (pool.Count == 0)
            {
                lock (_stateLock)
                {
                    _running = false;
                    _tabId = null;
                    _total = 0; _ok = 0; _fail = 0;
                }
                _gate.StopMove("");
                _logs.Add("❌ 纯移动任务循环已停止：候选站点池为空（请检查「选点范围」）", "#f87171");
                Broadcast();
                return;
            }

            var st = pool[Random.Shared.Next(pool.Count)];
            var settings = _settings.Get();
            if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            {
                _logs.Add("❌ 纯移动任务：未配置 GRCS 地址（地图信息页系统设置），等待下轮重试", "#f87171");
            }
            else
            {
                var payload = new VehicleOrderRequest
                {
                    CreateTime = DateTime.Now,
                    SceneName = settings.SceneName,
                    OrderType = "MOVE_ONLY",
                    OrderId = $"{_orderIdPrefix}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper()}_{Interlocked.Increment(ref _seq)}",
                    OrderName = "wcs模拟器纯移动任务",
                    VehicleName = null,
                    Priority = _priority,
                    StationCodes = [st.Mark],
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (ok, code, json) = await _grcs.SendVehicleOrderAsync(settings.GrcsBaseUrl, payload);
                sw.Stop();
                var elapsedMs = sw.ElapsedMilliseconds;
                var bodyJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                lock (_stateLock)
                {
                    _lastStation = st.Mark;
                    _total++;
                    if (ok)
                    {
                        _ok++;
                        _lastError = null;
                        _logs.AddOrUpdate("[纯移动·成功]", $"纯移动任务：下发成功，目的地: {st.Mark} ✓ 任务号：（{payload.OrderId}）（耗时 {elapsedMs}ms）\n请求体: {bodyJson}", "#4ade80");
                    }
                    else
                    {
                        _fail++;
                        var reason = code == 0 ? "⚠ 超时/网络异常" : $"HTTP {code}";
                        _lastError = reason;
                        _logs.AddOrUpdate("[纯移动·失败]", $"纯移动任务：{reason} · {st.Mark}（{payload.OrderId}）（耗时 {elapsedMs}ms）\n请求体: {bodyJson}\nGRCS 响应: {json}", "#f87171");
                    }
                }
            }

            Broadcast();
            if (!_running || ct.IsCancellationRequested) break;

            // 对齐节拍：dispatch 慢也不拖累下一轮（落后则从当前重新起算，不堆积）
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var intervalTicks = (long)_interval * 1000 * System.Diagnostics.Stopwatch.Frequency / 1000;
            if (now >= nextTick) nextTick = now + intervalTicks;
            var remainMs = (int)((nextTick - now) * 1000 / System.Diagnostics.Stopwatch.Frequency);
            if (remainMs > 0) { try { await Task.Delay(remainMs, ct); } catch { break; } }
            nextTick += intervalTicks;
        }
    }

    /// <summary>SignalR 广播纯移动任务状态（所有标签页共享一条连接，全部实时收到）。</summary>
    private void Broadcast() => _hub.Clients.All.SendAsync("MoveTaskStats", Snapshot());
}

/// <summary>纯移动任务循环启动参数（POST /api/wcs/auto/move/start）。</summary>
public class StartMoveReq
{
    public string? TabId { get; set; }
    public int Interval { get; set; }          // 秒
    public int Priority { get; set; }
    public string? OrderIdPrefix { get; set; }
}

/// <summary>纯移动任务循环状态（SignalR MoveTaskStats 广播 + GET status 字段）。</summary>
public class MoveTaskStatsDto
{
    public bool Running { get; set; }
    public string? TabId { get; set; }
    public int Interval { get; set; }
    public int Seq { get; set; }
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Fail { get; set; }
    public string? LastError { get; set; }
    public string? LastStation { get; set; }
}