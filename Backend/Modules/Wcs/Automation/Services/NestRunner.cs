using System.Text.Json;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Proxy.Services;
using WCSBackend.Modules.Wcs.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 归巢模式：地图框选巢区（站点 Mark 列表），持续调度直到巢区内所有目标点都被车占用（按钮触发，可停止）。
/// 流程：查 GRCS 全部车辆 → 过滤就绪车（在线 + IDLE + AUTOMATIC + 无当前任务）→
/// 用车辆坐标与巢区目标点（区域内全部启用站点，不限类型）坐标匹配 + location 精确匹配判定车是否在区内 →
/// 区域外（车队内）就绪车逐台下发 MOVE_ONLY 到区内空目标点（同层优先最近），下发成功的点立即标记「在途」不再派车，
/// 每轮巡检在途车：到达 → 转占用；仍在执行任务 → 永久等待（不因超时释放）；下线/报错/消失 → 释放重派。
/// 只调度本次车队（不动态补位其他车）：框选 N 个点最多 N 台车在途，车少于目标点时占多少算多少，
/// 车多于目标点时只保留前 N 台参与；直到区内目标点全部有车（或无车队内可调车 → 提示差 N 台结束）。
/// 运行中可停止（POST nest/stop）。统计/日志/状态经 SignalR（/hubs/task-stages）广播 NestStats 回推前端；运行中防重入。
/// </summary>
public class NestRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly GrcsHttpClient _grcs;
    private readonly MapStoreService _map;
    private readonly NestConfigService _nestConfig;
    private readonly WcsSettingsService _settings;
    private readonly AutomationLogService _logs;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    private readonly object _stateLock = new();
    private bool _running;
    private bool _completed;
    private string? _lastRunAt;
    private List<string> _readyVehicles = [];
    private int _ok;
    private int _fail;
    private string? _lastError;
    private int _targetTotal;
    private int _targetOccupied;
    private int _targetAssigned;
    /// <summary>已下发在途的目标点（目标点 Mark → 车辆名）；车到达前该点不再派车，永久等待不因超时释放。</summary>
    private Dictionary<string, string> _assignments = [];
    /// <summary>本次归巢车队（用户勾选的车；未勾选时首轮自动捕获当前就绪车）。只调度车队内车，不再动态补位；车数超过巢区目标点数时只保留前 N 台。</summary>
    private List<string> _pool = [];
    private CancellationTokenSource? _cts;

    /// <summary>轮询间隔：下发后等待车辆移动的时间。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);

    public NestRunner(GrcsHttpClient grcs, MapStoreService map, NestConfigService nestConfig,
        WcsSettingsService settings, AutomationLogService logs, IHubContext<TaskStageRealtimeHub> hub)
    {
        _grcs = grcs;
        _map = map;
        _nestConfig = nestConfig;
        _settings = settings;
        _logs = logs;
        _hub = hub;
    }

    // ── 状态（前端卡片 / status 接口共用）──
    public bool Running { get { lock (_stateLock) { return _running; } } }
    public string? LastRunAt { get { lock (_stateLock) { return _lastRunAt; } } }

    public NestStatsDto Snapshot()
    {
        lock (_stateLock)
        {
            return new NestStatsDto
            {
                Running = _running,
                LastRunAt = _lastRunAt,
                PoolVehicles = _pool.ToList(),
                Ok = _ok,
                Fail = _fail,
                LastError = _lastError,
                TargetTotal = _targetTotal,
                TargetOccupied = _targetOccupied,
                TargetAssigned = _targetAssigned,
                Completed = _completed,
            };
        }
    }

    /// <summary>执行一次归巢（异步后台）。运行中再次调用直接忽略。
/// <paramref name="poolVehicles"/> = 本次车队车名（前端多选）；null/空 = 首轮自动捕获当前就绪车为车队。</summary>
    public (bool Ok, string? Reason) Run(List<string>? poolVehicles = null)
    {
        lock (_stateLock)
        {
            if (_running) return (false, "归巢执行中，请稍候");
            _running = true;
            _completed = false;
            _ok = 0;
            _fail = 0;
            _lastError = null;
            _readyVehicles = [];
            _assignments = [];
            _targetAssigned = 0;
            _pool = poolVehicles?.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            _lastRunAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _cts = new CancellationTokenSource();
        }
        _ = RunCoreAsync();
        Broadcast();
        return (true, null);
    }

    /// <summary>停止归巢（中断等待与后续下发，本轮已下发的不撤销）。</summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _cts?.Cancel();
            _running = false;
            _lastError = "已手动停止";
        }
        _logs.Add("⏹ 归巢模式已手动停止", "#fbbf24");
        Broadcast();
    }

    private async Task RunCoreAsync()
    {
        try
        {
            var settings = _settings.Get();
            if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            {
                await FailAsync("未配置 GRCS 地址（地图信息页系统设置）");
                return;
            }

            // 巢区目标点：框选区域内的全部启用站点（不限类型）
            var marks = _nestConfig.Get().Marks;
            var stations = _map.GetStations();
            var markSet = new HashSet<string>(marks, StringComparer.OrdinalIgnoreCase);
            var targets = stations.Where(s => markSet.Contains(s.Mark) && s.StaEnable).ToList();
            lock (_stateLock) { _targetTotal = targets.Count; _targetOccupied = 0; }
            if (targets.Count == 0)
            {
                var reason = "巢区为空（请先在自动化任务→归巢模式用「地图框选巢区」选择区域并保存）";
                _logs.Add("❌ 归巢模式：" + reason, "#f87171");
                await FailAsync(reason);
                return;
            }

            // 车队车数 > 巢区目标点数：只保留前 N 台参与（覆盖「未选车 → 首轮捕获就绪车」场景）
            lock (_stateLock)
            {
                if (_pool.Count > targets.Count)
                {
                    var truncated = _pool.Take(targets.Count).ToList();
                    _logs.Add($"⚠️ 车队 {_pool.Count} 台 > 巢区目标点 {targets.Count} 个：仅前 {targets.Count} 台参与归巢，其余忽略（{string.Join("、", truncated)}）", "#fbbf24");
                    _pool = truncated;
                }
            }
            _logs.Add($"▶ 归巢模式开始：巢区 {marks.Count} 个 Mark，启用目标点 {targets.Count} 个（不限类型），车队 {_pool.Count} 台，持续调度直到目标点全被车占用（车少于目标点时占多少算多少，不另调车）", "#60a5fa");

            var minSpacing = EstimateGridSpacing(targets);
            var matchDist = Math.Max(minSpacing / 2.0, 0.5);   // 坐标匹配阈值：网格半距

            while (true)
            {
                if (_cts != null && _cts.IsCancellationRequested)
                {
                    await FinishAsync("⏹ 归巢模式已停止");
                    return;
                }

                // 1. 查全部车辆 → 就绪车
                var (vOk, vCode, vJson) = await _grcs.QueryVehiclesAsync(settings.GrcsBaseUrl, settings.SceneName);
                if (!vOk)
                {
                    var reason = vCode == 0 ? "⚠ 查询车辆超时/网络异常" : $"查询车辆 HTTP {vCode}";
                    _logs.Add("❌ 归巢模式：" + reason + $"\nGRCS 响应: {vJson}", "#f87171");
                    await FailAsync(reason);
                    return;
                }
                List<VehicleInfoDto> vehicles;
                try { vehicles = JsonSerializer.Deserialize<List<VehicleInfoDto>>(vJson, JsonOpts) ?? []; }
                catch { vehicles = []; }
                var ready = vehicles.Where(v => v.IsReady).ToList();
                lock (_stateLock) { _readyVehicles = ready.Select(v => v.Name).ToList(); }

                // 车队：未指定时首轮自动捕获当前就绪车（之后固定，只出不进）
                if (_pool.Count == 0)
                {
                    lock (_stateLock) { _pool = ready.Select(v => v.Name).ToList(); }
                    _logs.Add($"🚗 未指定车辆，自动捕获当前就绪车 {_pool.Count} 台为本次车队：{string.Join("、", _pool)}", "#60a5fa");
                }

                // 2. 判定每台就绪车所在目标点（location 精确匹配优先，坐标匹配兜底；一个点只认一台车）
                var occupiedTargets = new HashSet<MapStationLite>();
                foreach (var v in ready)
                {
                    var hit = targets.FirstOrDefault(t => string.Equals(t.Mark, v.Location, StringComparison.OrdinalIgnoreCase));
                    if (hit != null && !occupiedTargets.Contains(hit)) occupiedTargets.Add(hit);
                }
                foreach (var v in ready)
                {
                    MapStationLite? best = null;
                    var bestD = double.MaxValue;
                    foreach (var t in targets)
                    {
                        var d = Math.Sqrt(Math.Pow(v.X - t.X, 2) + Math.Pow(v.Y - t.Y, 2));
                        if (d < bestD) { bestD = d; best = t; }
                    }
                    if (best != null && bestD < matchDist && !occupiedTargets.Contains(best))
                        occupiedTargets.Add(best);
                }

                // 3. 巡检在途车：到达 → 转为已占用；车下线/ERROR/消失/空闲但不在点 → 释放该点；仍在执行任务 → 永久等待
                var released = new List<string>();
                foreach (var kv in _assignments.ToList())
                {
                    var veh = vehicles.FirstOrDefault(v => string.Equals(v.Name, kv.Value, StringComparison.OrdinalIgnoreCase));
                    var inPoint = veh != null
                        && (string.Equals(veh.Location, kv.Key, StringComparison.OrdinalIgnoreCase)
                            || targets.Any(t => string.Equals(t.Mark, kv.Key, StringComparison.OrdinalIgnoreCase)
                                && Math.Sqrt(Math.Pow(veh.X - t.X, 2) + Math.Pow(veh.Y - t.Y, 2)) < matchDist));
                    if (veh == null || veh.IsOnline == false || string.Equals(veh.ExecutionState, "ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        _assignments.Remove(kv.Key);
                        lock (_stateLock) { _pool.Remove(kv.Value); }
                        released.Add($"{kv.Value}（{kv.Key}）");
                    }
                    else if (inPoint)
                    {
                        _assignments.Remove(kv.Key);
                        var hit = targets.FirstOrDefault(t => string.Equals(t.Mark, kv.Key, StringComparison.OrdinalIgnoreCase));
                        if (hit != null && !occupiedTargets.Contains(hit)) occupiedTargets.Add(hit);
                        _logs.Add($"✅ 车辆 {kv.Value} 已到达目标点 {kv.Key}", "#4ade80");
                    }
                    // 任务执行中 → 永久等待（不因超时释放）
                }
                if (released.Count > 0)
                    _logs.Add($"↩ 释放目标点（车下线/报错/消失）：{string.Join("、", released)}，等待重新调度", "#fbbf24");

                var assignedMarks = new HashSet<string>(_assignments.Keys, StringComparer.OrdinalIgnoreCase);
                var freeTargets = targets.Where(t => !occupiedTargets.Contains(t) && !assignedMarks.Contains(t.Mark)).ToList();
                var assignedVehicles = new HashSet<string>(_assignments.Values, StringComparer.OrdinalIgnoreCase);
                var poolSet = new HashSet<string>(_pool, StringComparer.OrdinalIgnoreCase);
                var usableReady = ready.Where(v =>
                {
                    if (assignedVehicles.Contains(v.Name)) return false;
                    var nearest = targets.Select(t => Math.Sqrt(Math.Pow(v.X - t.X, 2) + Math.Pow(v.Y - t.Y, 2))).DefaultIfEmpty(double.MaxValue).Min();
                    return nearest >= matchDist;
                }).ToList();
                // 只调度车队内车（不动态补位其他就绪车；车少时占多少算多少，其余点保持空闲并结束）
                var outsideReady = usableReady.Where(v => poolSet.Contains(v.Name)).ToList();
                lock (_stateLock) { _targetOccupied = occupiedTargets.Count; _targetAssigned = _assignments.Count; }
                Broadcast();

                // 4. 终止条件
                if (occupiedTargets.Count >= targets.Count)
                {
                    _logs.Add($"✅ 归巢完成：巢区 {targets.Count} 个目标点已全部被车占用（成功 {_ok} / 失败 {_fail}）", "#4ade80");
                    await FinishAsync(null, true);
                    return;
                }
                if (outsideReady.Count == 0 && _assignments.Count == 0)
                {
                    _logs.Add($"🕓 巢区目标点 {targets.Count} 个，已占用 {occupiedTargets.Count} 个，但无区域外就绪车可调（还差 {targets.Count - occupiedTargets.Count} 台，车可能在执行任务/不在线/非空闲），本轮结束", "#fbbf24");
                    await FinishAsync(null, true);
                    return;
                }

                // 5. 逐台车队内就绪车 → 最近空目标点下发（一个点一台车；成功即标记在途，失败下轮重派）
                var dispatched = 0;
                foreach (var vehicle in outsideReady)
                {
                    if (freeTargets.Count == 0) break;
                    var target = freeTargets
                        .OrderBy(t => Math.Sqrt(Math.Pow(t.X - vehicle.X, 2) + Math.Pow(t.Y - vehicle.Y, 2)))
                        .First();
                    freeTargets.Remove(target);
                    if (await DispatchAsync(vehicle, target, settings))
                    {
                        _assignments[target.Mark] = vehicle.Name;
                        dispatched++;
                    }
                    lock (_stateLock) { _targetAssigned = _assignments.Count; }
                    Broadcast();
                }
                _logs.Add($"🔄 本轮下发 {dispatched} 台（车队 {_pool.Count} 台），巢区占用 {occupiedTargets.Count}/{targets.Count}，在途 {_assignments.Count}，等待 {PollInterval.TotalSeconds:0} 秒后继续巡检", "#60a5fa");

                // 6. 等待移动（在途车永久等待直到到达；停止时中断）
                try { await Task.Delay(PollInterval, _cts?.Token ?? CancellationToken.None); }
                catch (TaskCanceledException)
                {
                    await FinishAsync("⏹ 归巢模式已停止");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logs.Add("❌ 归巢模式异常：" + ex.Message, "#f87171");
            await FailAsync(ex.Message);
        }
    }

    /// <summary>估算普通路点网格最小间距（任意两点的最小距离；不足两点时取 1，防除零）。</summary>
    private static double EstimateGridSpacing(List<MapStationLite> roads)
    {
        if (roads.Count < 2) return 1;
        var spacing = double.MaxValue;
        for (var i = 0; i < roads.Count; i++)
        {
            for (var j = i + 1; j < roads.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(roads[i].X - roads[j].X, 2) + Math.Pow(roads[i].Y - roads[j].Y, 2));
                if (d > 0.001 && d < spacing) spacing = d;
            }
        }
        return spacing == double.MaxValue ? 1 : spacing;
    }

    /// <summary>下发 MOVE_ONLY。true = HTTP 2xx 下发成功（点标记在途）；false = HTTP 失败/超时（点下轮重派）。</summary>
    private async Task<bool> DispatchAsync(VehicleInfoDto vehicle, MapStationLite target, WcsSettingsDto settings)
    {
        var payload = new VehicleOrderRequest
        {
            CreateTime = DateTime.Now,
            SceneName = settings.SceneName,
            OrderType = "MOVE_ONLY",
            OrderId = $"NestHome_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper()}_{vehicle.Name.GetHashCode():x}",
            OrderName = "wcs模拟器归巢任务",
            VehicleName = vehicle.Name,
            Priority = 50,
            StationCodes = [target.Mark],
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
            if (ok)
            {
                _ok++;
                _lastError = null;
                _logs.AddOrUpdate("[归巢·成功]", $"车辆 {vehicle.Name} → {target.Mark}（{target.Floor} 层）✓ 任务号：（{payload.OrderId}）（耗时 {elapsedMs}ms）\n请求体: {bodyJson}", "#4ade80");
            }
            else
            {
                _fail++;
                var reason = code == 0 ? "⚠ 超时/网络异常" : $"HTTP {code}";
                _lastError = reason;
                _logs.AddOrUpdate("[归巢·失败]", $"车辆 {vehicle.Name} → {target.Mark}：{reason}（{payload.OrderId}）（耗时 {elapsedMs}ms）\n请求体: {bodyJson}\nGRCS 响应: {json}", "#f87171");
            }
        }
        return ok;
    }

    private void Finish() => _logs.Add($"✅ 归巢模式执行完成：共 {_readyVehicles.Count} 台车，成功 {_ok} / 失败 {_fail}", "#4ade80");

    private async Task FinishAsync(string? summary = null, bool completed = false)
    {
        if (!string.IsNullOrEmpty(summary)) _logs.Add(summary, "#4ade80");
        else Finish();
        lock (_stateLock)
        {
            if (completed)
            {
                _completed = true;
                _pool = [];
                _nestConfig.Set(new NestConfigDto());
                _logs.Add("🧹 归巢执行完成，已清空车队与巢区（下次归巢请重新框选）", "#4ade80");
            }
            _running = false;
            _cts?.Dispose();
            _cts = null;
        }
        Broadcast();
    }

    private async Task FailAsync(string reason)
    {
        lock (_stateLock) { _lastError = reason; _running = false; _cts?.Dispose(); _cts = null; }
        Broadcast();
    }

    /// <summary>SignalR 广播归巢状态。</summary>
    private void Broadcast() => _hub.Clients.All.SendAsync("NestStats", Snapshot());
}