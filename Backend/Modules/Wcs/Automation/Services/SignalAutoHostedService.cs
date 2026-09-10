using Backend.Shared.Infrastructure.Repository;
using WCSBackend.Modules.Wcs.Proxy.Services;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Console.Services;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 信号自动放行服务（由前端 SignalAutoService 平移，Skill E：leader 模式整个删除，后端天然唯一）。
/// 四档自动开关（进入申请/到达/移除/分拣）都在这一进程内，跨标签页天然一致；
/// 进入申请档与 AdmittanceService 共享（AllowEntry 读这里，SQLite 持久化）。
/// 每 3 秒消费台账 + 阶段事件：段1 FINISHED 且段2 已下发 → 向 GRCS 发 container_ready /
/// container_remove；分拣台 FINISHED → container_operation_finish。
/// </summary>
public class SignalAutoHostedService : IHostedService
{
    private readonly GrcsHttpClient _grcs;
    private readonly MapStoreService _mapStore;
    private readonly WcsSettingsService _settings;
    private readonly CargoCodeStore _cargoCodes;
    private readonly LedgerStore _ledger;
    private readonly AutomationLogService _logs;
    private readonly ITaskStageService _stages;
    private readonly IUnitOfWorkFactory _uow;
    private readonly SignalConfirmStore _confirm;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool ArrivalAuto { get; private set; }
    public bool RemovalAuto { get; private set; }
    public bool AutoSend { get; private set; }
    private bool Running { get; set; }
    private const int PollSeconds = 3;

    private HashSet<string> _arrivalConfirmed = [];
    private HashSet<string> _removalConfirmed = [];
    private HashSet<string> _ssSent = [];
    private bool _wasGrcsOffline;
    private static readonly System.Text.Json.JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public SignalAutoHostedService(GrcsHttpClient grcs, MapStoreService mapStore, WcsSettingsService settings,
        CargoCodeStore cargoCodes, LedgerStore ledger, AutomationLogService logs, ITaskStageService stages, IUnitOfWorkFactory uow,
        SignalConfirmStore confirm)
    {
        _grcs = grcs;
        _mapStore = mapStore;
        _settings = settings;
        _cargoCodes = cargoCodes;
        _ledger = ledger;
        _logs = logs;
        _stages = stages;
        _uow = uow;
        _confirm = confirm;
        LoadFlags();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Running = true;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Running = false;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void SetArrival(bool on) { ArrivalAuto = on; SaveFlags(); }
    public void SetRemoval(bool on) { RemovalAuto = on; SaveFlags(); }
    public void SetSorting(bool on) { AutoSend = on; SaveFlags(); }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, PollSeconds)));
        while (Running)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _logs.Add("❌ 信号自动放行异常: " + ex.Message, "#f87171"); }
            try { if (!await timer.WaitForNextTickAsync(ct)) break; } catch { break; }
        }
    }

    private async Task TickAsync()
    {
        if (!ArrivalAuto && !RemovalAuto && !AutoSend) return;
        // GRCS 离线熔断：暂停自动放行（不发起真实请求），状态翻转时记一条日志；恢复后自动继续
        if (_grcs.GrcsOnline == false)
        {
            if (!_wasGrcsOffline)
            {
                _logs.Add("⛔ GRCS 不可达，自动放行已暂停（GRCS 恢复后自动继续）", "#f59e0b");
                _wasGrcsOffline = true;
            }
            return;
        }
        _wasGrcsOffline = false;
        var mapStations = _mapStore.GetStations();
        if (mapStations.Count == 0) return;
        var finished = _stages.FinishedTaskIds;
        var ledger = _ledger.Get(2000);
        if (ArrivalAuto) await TickArrivalAsync(ledger, finished, mapStations);
        if (RemovalAuto) await TickRemovalAsync(ledger, finished, mapStations);
        if (AutoSend) await TickSortingAsync(ledger, mapStations);
    }

    // ── 货物到达：自动段1（空托入库 FINISHED）→ container_ready ──
    private async Task TickArrivalAsync(List<TaskLedgerEntry> ledger, HashSet<string> finished, List<MapStationLite> mapStations)
    {
        var settings = _settings.Get();
        var dispatched = ledger.Where(t => t.Ok && !string.IsNullOrEmpty(t.TaskId)).Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ledger)
        {
            if (string.IsNullOrEmpty(t.TaskId) || _arrivalConfirmed.Contains(t.TaskId)) continue;
            var isAuto = t.TaskId.StartsWith("SimAuto_", StringComparison.OrdinalIgnoreCase);
            var isManual = t.TaskId.StartsWith("SimManual_", StringComparison.OrdinalIgnoreCase);
            if (!isAuto && !isManual) continue;
            if (isAuto && t.TaskType != "CONTAINER_CARRY_INBOUND") continue;
            if (isManual && t.TaskType != "CARGO_CARRY_INBOUND") continue;
            if (isAuto && (!finished.Contains(t.TaskId) || !dispatched.Contains(Seg2Id(t.TaskId)))) continue;
            var st = isAuto ? t.StationCode.LastOrDefault() : t.StationCode.FirstOrDefault();
            var cargoCode = isAuto ? _cargoCodes.Ensure(t.TaskId) : t.CargoCode;
            var (ok, code, _) = await _grcs.SendContainerReadyAsync(settings.GrcsBaseUrl, new
            {
                MsgTime = DateTime.Now,
                Warehouse = t.Warehouse,
                TaskId = t.TaskId,
                ContainerCode = cargoCode,
                StationCode = ToWcsCode(st ?? "", mapStations),
            });
            if (ok)
            {
                lock (_lock) { _arrivalConfirmed.Add(t.TaskId); }
                _confirm.Set("arrival", t.TaskId, null);   // 写入 task_records 的 SIGNAL_ARRIVAL 阶段
                _logs.Add("✓ container_ready " + t.TaskId + "（货 " + cargoCode + "）", "#4ade80");
            }
            else _logs.Add("❌ container_ready " + t.TaskId + " 发送失败 HTTP " + code, "#f87171");
        }
    }

    // ── 货物移除：自动段1（带载出库 FINISHED）→ container_remove ──
    private async Task TickRemovalAsync(List<TaskLedgerEntry> ledger, HashSet<string> finished, List<MapStationLite> mapStations)
    {
        var settings = _settings.Get();
        var dispatched = ledger.Where(t => t.Ok && !string.IsNullOrEmpty(t.TaskId)).Select(t => t.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ledger)
        {
            if (string.IsNullOrEmpty(t.TaskId) || _removalConfirmed.Contains(t.TaskId)) continue;
            var isAuto = t.TaskId.StartsWith("SimAuto_", StringComparison.OrdinalIgnoreCase);
            var isManual = t.TaskId.StartsWith("SimManual_", StringComparison.OrdinalIgnoreCase);
            if (!isAuto && !isManual) continue;
            if (isAuto && t.TaskType != "CARGO_CARRY_OUTBOUND") continue;
            if (isManual && t.TaskType != "CONTAINER_CARRY_OUTBOUND") continue;
            if (isAuto && (!finished.Contains(t.TaskId) || !dispatched.Contains(Seg2Id(t.TaskId)))) continue;
            var st = isAuto ? t.StationCode.LastOrDefault() : t.StationCode.FirstOrDefault();
            var containerCode = string.IsNullOrEmpty(t.CargoCode) ? t.ContainerCode : t.CargoCode;
            var (ok, code, _) = await _grcs.SendContainerRemoveAsync(settings.GrcsBaseUrl, new
            {
                MsgTime = DateTime.Now,
                Warehouse = t.Warehouse,
                ContainerCode = containerCode,
                StationCode = ToWcsCode(st ?? "", mapStations),
            });
            if (ok)
            {
                lock (_lock) { _removalConfirmed.Add(t.TaskId); }
                _confirm.Set("removal", t.TaskId, null);   // 写入 task_records 的 SIGNAL_REMOVAL 阶段
                _logs.Add("✓ container_remove " + t.TaskId + "（容器 " + containerCode + "）", "#4ade80");
            }
            else _logs.Add("❌ container_remove " + t.TaskId + " 发送失败 HTTP " + code, "#f87171");
        }
    }

    // ── 分拣完成：FINISHED 且站点是分拣台 → container_operation_finish ──
    private async Task TickSortingAsync(List<TaskLedgerEntry> ledger, List<MapStationLite> mapStations)
    {
        var settings = _settings.Get();
        var events = _stages.GetEventsSince(0);
        var finishedList = events.Where(e => e.Stage == "FINISHED").OrderBy(e => e.Time).ToList();
        if (finishedList.Count == 0) return;
        foreach (var e in finishedList)
        {
            if (string.IsNullOrEmpty(e.TaskId) || _ssSent.Contains(e.TaskId)) continue;
            var station = mapStations.FirstOrDefault(s => s.Mark == e.StationCode)
                ?? mapStations.FirstOrDefault(s => { var raw = e.StationCode; if (raw.Length > 2 && (raw[^2..] is "_0" or "_1")) raw = raw[..^2]; return s.Mark == raw; });
            if (station == null) continue;
            if ((station.StationType & (MapStationTypeBits.PickingStation | MapStationTypeBits.PeopleStation)) == 0) continue;
            var sendTaskId = e.TaskId + "_R";
            var (ok, code, _) = await _grcs.SendOperationFinishAsync(settings.GrcsBaseUrl, new
            {
                MsgTime = DateTime.Now,
                Warehouse = e.Warehouse,
                TaskId = sendTaskId,
                ContainerCode = e.ContainerCode,
                RemoveContainer = false,
                StationCode = "",
                AreaCode = "",
            });
            if (!ok) { _logs.Add("❌ 分拣 " + sendTaskId + " 发送失败 HTTP " + code, "#f87171"); continue; }
            lock (_lock) { _ssSent.Add(e.TaskId); }
            // 写入 task_records 的 SIGNAL_SENT 阶段，作为重启后的去重依据。
            var paramsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                returnTaskId = sendTaskId,
                removeContainer = false,
                destStation = "",
                destArea = "",
            });
            _confirm.Set("sent", e.TaskId, paramsJson);
            // 回库任务写台账（TaskType=SORTING_RETURN，对应分拣段1 TaskId+"_R"，供任务看板关联显示）
            var seg1 = ledger.FirstOrDefault(x => x.TaskId == e.TaskId);
            await _ledger.AppendAsync([new TaskLedgerEntry
            {
                TaskId = sendTaskId,
                TaskType = "SORTING_RETURN",
                ContainerCode = seg1?.ContainerCode ?? e.ContainerCode,
                CargoCode = seg1?.CargoCode ?? "",
                StationCode = [],
                Warehouse = e.Warehouse,
                Time = DateTime.Now.ToString("O"),
                Ok = true,
                StatusCode = code
            }]);
            _logs.Add("✓ container_operation_finish " + sendTaskId, "#4ade80");
        }
    }

    private static string Seg2Id(string seg1Id)
    {
        if (seg1Id.StartsWith("SimAuto_", StringComparison.OrdinalIgnoreCase) && seg1Id.Length >= 2 && seg1Id[^1] == 'a')
            return seg1Id[..^1] + "b";
        return seg1Id;
    }

    private static string ToWcsCode(string mark, List<MapStationLite> mapStations)
    {
        var st = mapStations.FirstOrDefault(s => s.Mark.Equals(mark, StringComparison.OrdinalIgnoreCase));
        return st?.ToWcsCode() ?? mark;
    }

    private void LoadFlags()
    {
        try
        {
            ArrivalAuto = bool.TryParse(KvAccess.Get(_uow, "sig_arrival_auto"), out var a) && a;
            RemovalAuto = bool.TryParse(KvAccess.Get(_uow, "sig_removal_auto"), out var r) && r;
            AutoSend = bool.TryParse(KvAccess.Get(_uow, "sig_ss_auto"), out var s) && s;
            // 一次性迁移：旧 kv 确认集合 → task_records 阶段（幂等 Set，仅插入缺失项）
            MigrateLegacy("sig_arrival_confirmed", "arrival");
            MigrateLegacy("sig_removal_confirmed", "removal");
            MigrateLegacy("sig_ss_sent", "sent");
            _arrivalConfirmed = LoadConfirmSet("arrival");
            _removalConfirmed = LoadConfirmSet("removal");
            _ssSent = LoadConfirmSet("sent");
        }
        catch { }
    }

    /// <summary>旧 kv 集合迁移到任务阶段记录（Set 幂等，重复调用无副作用）。</summary>
    private void MigrateLegacy(string kvKey, string kind)
    {
        foreach (var id in LoadSet(kvKey))
            _confirm.Set(kind, id, null);
    }

    private HashSet<string> LoadConfirmSet(string kind)
        => _confirm.GetAll().TryGetValue(kind, out var rows)
            ? rows.Select(r => r.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private void SaveFlags()
    {
        try
        {
            KvAccess.Set(_uow, "sig_arrival_auto", ArrivalAuto.ToString().ToLowerInvariant());
            KvAccess.Set(_uow, "sig_removal_auto", RemovalAuto.ToString().ToLowerInvariant());
            KvAccess.Set(_uow, "sig_ss_auto", AutoSend.ToString().ToLowerInvariant());
        }
        catch { }
    }

    private HashSet<string> LoadSet(string key)
    {
        try
        {
            var s = KvAccess.Get(_uow, key);
            if (!string.IsNullOrEmpty(s))
                return System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(s, Opts) ?? [];
        }
        catch { }
        return [];
    }
}
