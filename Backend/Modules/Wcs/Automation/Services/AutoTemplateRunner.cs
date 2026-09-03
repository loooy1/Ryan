using System.Collections.Concurrent;
using System.Text.Json;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;
using InvItem = GrcsBackend.Modules.Wcs.Infrastructure.WcsInventoryStore.InvItem;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Proxy.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 自动化模板执行引擎（取代旧的硬编码两段式 AutoRunHostedService + ContainerTaskRunner）。
/// 用户在前端「自动化」页创建模板：模板 = 线性有序步骤，每步从
/// 选托盘(PickPallet) / 选货物(PickCargo) / 执行任务模板(RunTemplate) 中选。
/// 执行时按步骤顺序进行：选托盘/选货物从范围内库存快照随机抽取（取出后从池移除，避免同轮复用）；
/// 执行任务模板时用前置步骤挑出的托盘/货物填充容器与起点，并按模板类型选终点，下发单个 GRCS 任务。
/// 模板的「起点模块」在发送前执行、『起点之后模块』在发送成功后执行、『终点模块』在任务 FINISHED 后执行，
/// 三类模块统一经 ModuleRunService（后端执行器）完成，不再依赖前端。
/// 轮询模式：Start 后每 Interval 毫秒执行一次当前模板；单次模式：ExecuteOnce 立即执行 N 次。
/// 通过 AutomationGate 与移动任务循环互斥，单实例串行（同一时刻只跑一个模板），杜绝并发下发。
/// </summary>
public class AutoTemplateRunner : IHostedService
{
    private readonly MapStoreService _map;
    private readonly RangeConfigService _range;
    private readonly WcsSettingsService _settings;
    private readonly StationLockStore _locks;
    private readonly AutomationLogService _log;
    private readonly ITaskStageService _stage;
    private readonly AutomationGate _gate;
    private readonly ModuleRunService _modules;
    private readonly TaskTemplateStore _taskTemplates;
    private readonly AutoTemplateStore _templates;
    private readonly MockRuleStore _mocks;
    private readonly GrcsInventoryCacheService _inventoryCache;
    private readonly WcsInventoryStore _invStore;
    private readonly ILogger<AutoTemplateRunner> _logger;

    // 选点/加锁/占用合并临界区：多模板并发串行化「选点→加锁」，避免竞态选中同一点；occupied 合并计算同锁保护
    private readonly object _chainLock = new();

private readonly object _stateLock = new();
    private bool _running;
    private volatile bool _halted; // 强制结束（全部重置）专用：在途轮次的链式步骤立即停止下发；仅「停止轮询」不影响已启动轮次
    private int _interval = 3000;
    private List<string> _activeTemplateIds = new();
    private int _executed;
    private int _roundSeq; // 成功轮数（选托盘/选货物成功后才 +1）
    private string _status = "未启动";
    private string? _activeTabId;
    private CancellationTokenSource? _cts;

    // 任务号 → 锁定的起点 mark（FINISHED 后释放）
    private readonly ConcurrentDictionary<string, string> _lockByTask = new(StringComparer.OrdinalIgnoreCase);

    // 任务号 → 锁定的终点 mark（FINISHED 后释放）
    private readonly ConcurrentDictionary<string, string> _endLockByTask = new(StringComparer.OrdinalIgnoreCase);
    // 容器占用/选池已迁移到 WcsInventoryStore 账本（SQLite 持久化），不再使用内存集合。

    public AutoTemplateRunner(
        MapStoreService map, RangeConfigService range, WcsSettingsService settings,
        StationLockStore locks, AutomationLogService log, ITaskStageService stage, AutomationGate gate,
        ModuleRunService modules, TaskTemplateStore taskTemplates, AutoTemplateStore templates,
        MockRuleStore mocks, GrcsInventoryCacheService inventoryCache, WcsInventoryStore invStore,
        ILogger<AutoTemplateRunner> logger)
    {
        _map = map; _range = range; _settings = settings; _locks = locks; _log = log;
        _stage = stage; _gate = gate; _modules = modules; _taskTemplates = taskTemplates;
        _templates = templates; _mocks = mocks; _inventoryCache = inventoryCache; _invStore = invStore;
        _logger = logger;
    }

    // ── 状态 ──
    public bool Running => _running;
    public int Interval => _interval;
    public string? ActiveTemplateId => _activeTemplateIds.FirstOrDefault();
    public string? ActiveTemplateName => string.Join(" + ", _activeTemplateIds.Select(id => _templates.GetAll().FirstOrDefault(t => t.Id == id)?.Name).Where(n => n != null));
    public int Executed => _executed;
    public string Status => _status;
    public string? ActiveTabId => _activeTabId;

    public AutoTemplateStatusDto Snapshot()
        => new()
        {
            Running = _running,
            Interval = _interval,
            ActiveTemplateId = ActiveTemplateId,
            ActiveTemplateName = ActiveTemplateName,
            ActiveTabId = _activeTabId,
            Executed = _executed,
            Status = _status,
            GateAuto = _gate.AutoRunning,
            AnyRunning = _gate.AnyRunning,
        };

    Task IHostedService.StartAsync(CancellationToken ct) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken ct) { Stop(); return Task.CompletedTask; }

public (bool ok, string reason) Start(string? tabId, List<string>? templateIds)
    {
        if (!_mocks.HasTaskStageRule())
        {
            const string reason = "未配置任务阶段卡（Mock 卡片勾选「关联任务看板」），禁止启动轮询，请先在信号交互→通用 Mock 入站配置";
            _log.Add("启动失败：" + reason, "#f87171");
            return (false, reason);
        }
        lock (_stateLock)
        {
            if (_running)
            {
                _log.Add("启动失败：轮询已在进行中", "#f87171");
                return (false, "轮询已在进行中");
            }
        }
        var ids = (templateIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count == 0)
        {
            _log.Add("启动失败：未指定自动化模板", "#f87171");
            return (false, "未指定自动化模板");
        }
        var all = _templates.GetAll();
        var missing = ids.FirstOrDefault(id => !all.Any(t => t.Id == id));
        if (missing != null)
        {
            _log.Add($"启动失败：模板不存在 {missing}", "#f87171");
            return (false, $"模板不存在 {missing}");
        }
        if (!_gate.TryStartAuto(tabId))
        {
            _log.Add("启动被拒：移动任务循环或批量任务正在运行", "#f87171");
            return (false, "移动任务循环或批量任务正在运行");
        }
        lock (_stateLock)
        {
_activeTemplateIds = ids;
            _activeTabId = tabId;
            _running = true;
            _halted = false;
            _status = "轮询中";
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _log.Add($"自动化模板轮询启动：{ActiveTemplateName}", "#4ade80");
        _ = PollLoop(_cts.Token);
        // 库存账本初始化：账本空 → 从 GRCS 全量建账；选点范围新增点位 → 增量合并；无变化 → 不查 GRCS。
        _ = InitLedgerAsync();
        return (true, "");
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_running) return;
            _running = false;
            _status = "已停止";
        }
        _gate.StopAuto();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _log.Add("自动化模板轮询停止", "#fbbf24");
    }

    /// <summary>
    /// 库存账本初始化（Start 时异步执行）：账本空 → 从 GRCS 全量建账；
    /// 选点范围新增点位 → 增量合并（已有点位不动账本）；无变化 → 不查 GRCS。
    /// </summary>
    private async Task InitLedgerAsync()
    {
        try
        {
            var range = _range.Get();
            var marks = (range.Enabled && range.Marks.Count > 0)
                ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
                : null;
            if (_invStore.All().Count == 0)
            {
                _log.Add("库存账本为空，正在从 GRCS 全量同步建立账本…", "#fbbf24");
                var records = await _inventoryCache.SyncAllAsync();
                if (records.Count == 0 && !_inventoryCache.Ready)
                {
                    _log.Add("库存同步失败（GRCS 未响应），等待下轮重试", "#f87171");
                    return;
                }
                _invStore.RebuildFromGrcs(records);
                _invStore.SetSyncedMarks(marks ?? new HashSet<string>());
                _log.Add($"库存账本已建立：{records.Count} 条", "#4ade80");
                return;
            }
            if (marks == null) return;   // 范围未启用：无 diff 概念，不查 GRCS
            var synced = _invStore.GetSyncedMarks();
            var added = marks.Except(synced).ToList();
            if (added.Count == 0) return;   // 选点无变化，直接用账本
            _log.Add($"选点范围新增 {added.Count} 个点位，正在增量同步库存…", "#fbbf24");
            var all = await _inventoryCache.SyncAllAsync();
            if (!_inventoryCache.Ready)
            {
                _log.Add("库存同步失败（GRCS 未响应），沿用现有账本", "#f87171");
                return;
            }
            _invStore.SyncMerge(all, marks);
            _invStore.SetSyncedMarks(marks);
            _log.Add("选点范围库存已增量合并", "#4ade80");
        }
        catch (Exception ex)
        {
            _log.Add("库存账本初始化异常：" + ex.Message, "#f87171");
        }
    }

    /// <summary>强制结束：停止轮询、无限等待的本轮任务不再等待 FINISHED、释放所有站点锁与容器占用、清空所有自动化日志。</summary>
    public void ForceEnd()
    {
lock (_stateLock)
        {
            _running = false;
            _halted = true;
            _status = "已强制结束";
        }
        _gate.StopAuto();
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        // 解除所有无限等待的 FINISHED 阻塞，使 WaitRoundCompletion 得以继续并自动清除
        try { _stage.ForceCompleteAll(); } catch { }
        // 释放所有站点锁与容器占用
        foreach (var kv in _lockByTask) { try { _locks.Release(kv.Value); } catch { } }
        foreach (var kv in _endLockByTask) { try { _locks.Release(kv.Value); } catch { } }
        _lockByTask.Clear();
        _endLockByTask.Clear();
        _invStore.ClearBusy();   // 账本占用全部释放（持久化）
        // 清空所有自动化轮次日志
        _log.Clear();
        _log.Add("⚠ 已强制结束当前轮次：所有任务不再等待 FINISHED，相关占用已释放", "#f87171");
    }

    public void SetInterval(int ms)
    {
        lock (_stateLock) _interval = Math.Clamp(ms, 200, 600_000);
    }

    /// <summary>单次执行（保留给 /api/wcs/auto/execute）：立即按模板下发一次；count 仅控制重复轮数（默认 1）。每次执行是一个独立轮次。</summary>
    public async Task ExecuteOnce(string? templateId, int count, int? intervalMs, string? tabId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return;
        if (_gate.AutoRunning) { _log.Add("单次执行被拒：轮询自动化正在运行，请先停止轮询", "#f87171"); return; }
        if (_gate.MoveRunning) { _log.Add("单次执行被拒：纯移动任务循环正在运行", "#f87171"); return; }
        var tpl = _templates.GetAll().FirstOrDefault(t => t.Id == templateId);
        if (tpl == null) return;
        var n = Math.Clamp(count, 1, 200);
        for (var i = 0; i < n; i++)
        {
            var pendingNo = _roundSeq + 1;
            var parentId = _log.BeginRound($"第 {pendingNo} 轮 · 手动执行");
            var (ids, details) = await RunTemplates(new List<AutoTemplateDto> { tpl }, parentId);
            if (ids.Count > 0) _ = WaitRoundCompletion(ids, details, parentId);
            if (intervalMs is > 0 && i < n - 1) await Task.Delay(intervalMs.Value);
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested) break;
            if (!_running) { try { await Task.Delay(200, ct); } catch { break; } continue; }
            var tpls = _activeTemplateIds
                .Select(id => _templates.GetAll().FirstOrDefault(t => t.Id == id))
                .Where(t => t != null)
                .ToList();
            if (tpls.Count == 0) { try { await Task.Delay(_interval, ct); } catch { break; } continue; }
            var pendingNo = _roundSeq + 1;
            var parentId = _log.BeginRound($"第 {pendingNo} 轮 · 自动循环");
            _ = RunRoundAsync(tpls!, parentId);
            try { await Task.Delay(_interval, ct); } catch { break; }
        }
    }

/// <summary>跑完一轮（可能含等待 FINISHED 的步骤），然后等该轮所有任务完成后清除标题与日志。</summary>
    private async Task RunRoundAsync(List<AutoTemplateDto> tpls, string parentId)
    {
        // ForceEnd 后遗留轮次立即退出：不再选点/下发/写入锁定，避免「全部重置」后锁定明细残留
        if (!_running) return;
        try
        {
var (ids, details) = await RunTemplates(tpls, parentId);
            if (ids.Count > 0) await WaitRoundCompletion(ids, details, parentId);
            else _log.ClearRound(parentId);   // 空轮次（无库存/无候选等）不留空标题，避免日志堆积
        }
        catch (Exception ex)
        {
            _log.Add(parentId, $"轮次执行异常：{ex.Message}", "#f87171");
            _log.CompleteRound(parentId);
        }
    }

    /// <summary>等待本轮所有任务 FINISHED（无限等待），完成后清除整轮日志（含所有子任务）并在系统通知中提示详细信息。</summary>
    private async Task WaitRoundCompletion(List<string> taskIds, IReadOnlyDictionary<string, string> taskDetails, string parentId)
    {
        var waitAll = Task.WhenAll(taskIds.Select(async id => { try { await _stage.WaitFinishedAsync(id); } catch { } }));
        await waitAll;
        var parent = _log.GetRounds().FirstOrDefault(r => r.RoundId == parentId);
        var title = parent?.Title ?? parentId;
        var start = parent?.StartTime ?? "";
        var end = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string detailStr;
        if (taskDetails.Count > 0)
        {
            var i = 0;
            detailStr = string.Join("；", taskDetails.Select(kv => $"[{++i}] {kv.Value} ({kv.Key})"));
        }
        else
            detailStr = string.Join("、", taskIds);
        _log.Add($"✓ {title} 全部完成 — 发起 {start} · 完成 {end} · 共 {taskIds.Count} 个任务：{detailStr} ，日志已自动清除", "#4ade80");
        _log.ClearRound(parentId);
    }

    /// <summary>按模板集合执行一轮（一次轮询下发一次选中的模板实例）。
    /// 共用同一份库存快照：每个模板各自一条执行链(ctx)，选托盘/选货物在 lock 下从共享池抽取并移除，
    /// 因此多个模板不会选到同一托盘/货物（避免并发撞车）；各模板的 RunTemplateStep 并发下发到 GRCS。
    /// 单选 = 一个模板下发一次；多选 = 同时下发多个模板（间隔到了再整体来一轮）。</summary>
    private async Task<(List<string> ids, IReadOnlyDictionary<string, string> details)> RunTemplates(List<AutoTemplateDto> tpls, string roundId)
    {
        var taskIds = new ConcurrentBag<string>();
        var taskDetails = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tpls == null || tpls.Count == 0) return (taskIds.ToList(), taskDetails);
        var pendingNo = _roundSeq + 1;
        bool renamed = false;
        // 本轮是否真正需要库存（含选托盘/选货物步骤）。纯 RunTemplate（直接入库）模板不依赖库存，不应因库存为空而跳过。
        var needsInventory = tpls.Any(t => t.Steps.Any(s => s.Kind == AutoStepKinds.PickPallet || s.Kind == AutoStepKinds.PickCargo || s.Kind == AutoStepKinds.PickLoadedPallet));
        int? seq = null;
        void MarkFirst(string? childId, string name)
        {
            if (!seq.HasValue) { seq = Interlocked.Increment(ref _roundSeq); }
            if (!string.IsNullOrEmpty(childId)) _log.RenameRound(childId, name);
        }
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
        {
            var cid = _log.BeginRound("未配置 GRCS", roundId);
            _log.Add(cid, "未配置 GRCS 地址（连接设置页填写）", "#f87171");
            return (taskIds.ToList(), taskDetails);
        }
        var (emptyPallets, loadedPallets, cargos, snapOk, diagStored, diagLocked, diagLoaded, diagSnapAt) = await SnapshotAsync(settings, roundId);
        // 池空诊断文本：储位托盘总数/锁定/带货托 + 缓存快照年龄（定位「储位有托盘却池空」）
        var diagText = diagSnapAt == DateTime.MinValue
            ? "储位托盘未知（缓存未就绪）"
            : $"储位托盘共 {diagStored}（锁定 {diagLocked}、带货托 {diagLoaded}）｜快照 {(int)(DateTime.Now - diagSnapAt).TotalSeconds} 秒前";
        if (needsInventory && emptyPallets.Count == 0 && loadedPallets.Count == 0 && cargos.Count == 0)
        {
            // 无库存：不进轮次日志，只在系统通知中更新一条（时间随每轮刷新，可看到最后一次检查时间）；查询失败与真空区分提示
            var reason = snapOk
                ? "无可用库存（空托/带货托/货物均为 0），等待库存补充后自动下发"
                : "库存查询失败（GRCS 未响应），等待下轮重试";
            _log.AddOrUpdate("[无库存]", $"自动化轮询：{reason}", "#fbbf24");
            return (taskIds.ToList(), taskDetails);
        }

// 当前被货/托盘占用的站点由每步选点前重建（BuildOccupiedFromCache，缓存权威），不再共享

        var poolLock = new object();

        async Task RunOne(AutoTemplateDto tpl, int k, string childId)
        {
            var name = tpl.Name;
var ctx = new ExecCtx();
            // 本模板选过并加入黑名单的容器号（链结束时回收未成功下发的，防黑名单泄漏）
            var pickedBusy = new List<string>();
            bool first = true;
            try
            {
for (var i = 0; i < tpl.Steps.Count; i++)
            {
                var step = tpl.Steps[i];
                var stepNo = $"{i + 1}/{tpl.Steps.Count}";
                try
                {
                    if (first && step.Kind == AutoStepKinds.RunTemplate) MarkFirst(childId, name);
                    first = false;
                    // 步骤开始日志：动作类型 + 关键配置（容器来源/起点来源/等待标志），弹窗内可见完整执行链
                    var desc = step.Kind switch
                    {
                        AutoStepKinds.PickPallet => $"选托盘（{step.PalletFilter}）",
                        AutoStepKinds.PickCargo => "选货物",
                        AutoStepKinds.PickLoadedPallet => "选带货托",
                        AutoStepKinds.RunTemplate => $"执行任务模板「{TaskTemplateLabel(step.TemplateValue)}」({step.TemplateValue}) · 容器={(step.PickedStepIndex > 0 ? $"第{step.PickedStepIndex}步挑选" : (step.PickedStepIndex == -1 || step.UsePickedContainer) ? "前置挑选" : "自动生成")} · 起点={(step.UsePickedStart ? "前置终点" : "类型选点")} · 等待完成={(step.WaitForFinish ? "是" : "否")}",
                        _ => step.Kind,
                    };
                    _log.Add(childId, $"▶ 步骤 {stepNo}：{desc}", "#38bdf8");
                    if (step.Kind == AutoStepKinds.PickPallet)
                    {
                        InvItem? pick;
                        lock (poolLock)
                        {
                            var pool = step.PalletFilter switch
                            {
                                "Loaded" => loadedPallets,
                                "Any" => emptyPallets.Concat(loadedPallets).ToList(),
                                _ => emptyPallets,
                            };
                            if (pool.Count == 0) { _log.Add(childId, $"步骤 {stepNo} 选托盘失败：{step.PalletFilter} 池为空，等待下轮下发｜诊断：{diagText}", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{name}」：选托盘失败（{step.PalletFilter} 池为空），等待下轮下发", "#f87171"); return; }
                            pick = pool[Random.Shared.Next(pool.Count)];
                            emptyPallets.Remove(pick); loadedPallets.Remove(pick);
                            _invStore.Pick(pick.Code);   // 账本占用（picked，持久化）
                            pickedBusy.Add(pick.Code);
                        }
ctx.PalletCode = pick.Code; ctx.PalletMark = pick.Mark; ctx.ContainerCode = pick.Code;
                        ctx.PickedByStep[i + 1] = pick.Code;
                        ctx.LastEndMark = pick.Mark;
                        _log.Add(childId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
                        MarkFirst(childId, name);
                    }
                    else if (step.Kind == AutoStepKinds.PickCargo)
                    {
                        InvItem? pick;
                        lock (poolLock)
                        {
                            if (cargos.Count == 0) { _log.Add(childId, $"步骤 {stepNo} 选货物失败：货物池为空，等待下轮下发", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{name}」：选货物失败（货物池为空），等待下轮下发", "#f87171"); return; }
                            pick = cargos[Random.Shared.Next(cargos.Count)];
                            cargos.Remove(pick);
                            _invStore.Pick(pick.Code);   // 账本占用（picked，持久化）
                            pickedBusy.Add(pick.Code);
                        }
ctx.CargoCode = pick.Code; ctx.CargoMark = pick.Mark; ctx.ContainerCode = pick.Code;
                        ctx.PickedByStep[i + 1] = pick.Code;
                        ctx.LastEndMark = pick.Mark;
                        _log.Add(childId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
                        MarkFirst(childId, name);
                    }
                    else if (step.Kind == AutoStepKinds.PickLoadedPallet)
                    {
                        InvItem? pick;
                        lock (poolLock)
                        {
                            if (loadedPallets.Count == 0) { _log.Add(childId, $"步骤 {stepNo} 选带货托失败：带货托池为空，等待下轮下发｜诊断：{diagText}", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{name}」：选带货托失败（带货托池为空），等待下轮下发", "#f87171"); return; }
                            pick = loadedPallets[Random.Shared.Next(loadedPallets.Count)];
                            loadedPallets.Remove(pick);
                            _invStore.Pick(pick.Code);   // 账本占用（picked，持久化；托盘号 = 主容器单元）
                            if (!string.IsNullOrEmpty(pick.CargoCode)) _invStore.Pick(pick.CargoCode);
                            pickedBusy.Add(pick.Code);
                            if (!string.IsNullOrEmpty(pick.CargoCode)) pickedBusy.Add(pick.CargoCode);
                        }
var cargoCode = pick.CargoCode ?? pick.Code;
                        ctx.PalletCode = pick.Code; ctx.PalletMark = pick.Mark;
                        ctx.CargoCode = cargoCode; ctx.CargoMark = pick.Mark;
                        ctx.ContainerCode = cargoCode; // 带货托取货物号而非托盘号
                        ctx.PickedByStep[i + 1] = cargoCode;
                        ctx.LastEndMark = pick.Mark;
                        _log.Add(childId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
                        MarkFirst(childId, name);
                    }
                    else if (step.Kind == AutoStepKinds.RunTemplate)
                    {
var tid = await RunTemplateStep(step, ctx, settings, childId, stepNo, taskIds, taskDetails);
                        if (tid != null)
                        {
                            ctx.PickedByStep[i + 1] = ctx.ContainerCode ?? "";
                            MarkFirst(childId, name);
                        }
                    }
                }
catch (Exception ex)
                {
                    _log.Add(childId, $"步骤 {stepNo} 执行异常：{ex.Message}", "#f87171");
                }
                }
                _log.Add(childId, $"✓ 模板「{name}」步骤链完成（{tpl.Steps.Count} 步处理完毕）", "#4ade80");
            }
            finally
            {
                // 回收本模板选过但未下发的容器（picked 残留 = 未下发/下发失败已标记 fail → picked 直接放回）；
                // 下发成功的已 MarkBusy（busy 保留到 FINISHED），不受影响
                if (pickedBusy.Count > 0)
                {
                    _invStore.RecyclePicked(pickedBusy);
                }
            }
        }

        var tasks = new List<Task>(tpls.Count);
        for (int k = 0; k < tpls.Count; k++)
        {
            var idx = k;
            var cid = _log.BeginRound(tpls[idx].Name, roundId);
            tasks.Add(Task.Run(() => RunOne(tpls[idx], idx, cid)));
        }
        await Task.WhenAll(tasks);
        return (taskIds.ToList(), taskDetails);
    }

    /// <summary>保存前校验：每个 RunTemplate 步骤引用的任务模板，
    /// ① 终点类型必须在选点范围内存在对应站点；
    /// ② 起点类型（无论是否取自前置终点）必须在选点范围内存在对应站点；
    /// ③ 若「起点取自前置终点」且其起点类型已配置，需与前一步的终点类型兼容（有交集），否则链路衔接会失败；
    /// ④ 结构校验：「使用前置挑选的容器」前面必须有选托盘/选货物步骤；「起点取自前置终点」不能作为第一步。
    /// 校验失败返回错误列表，保存接口据此拒绝保存。</summary>
    public List<string> ValidateTemplates(List<AutoTemplateDto> items)
    {
        var errors = new List<string>();
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
            : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null
            ? allStations
            : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();

        bool HasType(int bits) => bits != 0 && stations.Any(s => (s.StationType & bits) != 0);

        foreach (var tpl in items ?? [])
        {
            TaskTemplateDto? prevTt = null;
            var seenPick = false;
            for (var i = 0; i < tpl.Steps.Count; i++)
            {
                var step = tpl.Steps[i];
                if (step.Kind == AutoStepKinds.PickPallet || step.Kind == AutoStepKinds.PickCargo || step.Kind == AutoStepKinds.PickLoadedPallet)
                {
                    // 选托盘/选货物：提供前置容器与“终点”（库存所在站，类型动态）；记录已出现，供后续步骤校验
                    seenPick = true;
                    prevTt = null;
                    continue;
                }
                if (step.Kind != AutoStepKinds.RunTemplate)
                {
                    prevTt = null;
                    continue;
                }
                var tt = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
                if (tt == null)
                {
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}：引用的任务模板不存在（{step.TemplateValue}）");
                    prevTt = null;
                    continue;
                }

// 结构校验：前置步骤存在性
                if (step.PickedStepIndex > 0 && step.PickedStepIndex >= i + 1)
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：容器来源引用了第 {step.PickedStepIndex} 步，但只能引用前置步骤（当前第 {i + 1} 步）");
                if (step.PickedStepIndex == -1 || (step.PickedStepIndex == 0 && step.UsePickedContainer))
                {
                    if (!seenPick)
                        errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：容器使用「最近前置挑选」，但其前面没有选托盘/选货物步骤");
                }
                if (step.UsePickedStart && i == 0)
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：勾选了「起点取自前置终点」，但不能作为第一步（前面没有可衔接的步骤）");

                // 终点：始终校验范围内有对应类型站点
                var eb = tt.End?.StationTypeBits ?? 0;
                if (eb != 0 && !HasType(eb))
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：终点类型「{BitsName(eb)}」在选点范围内无匹配站点");

                // 起点：无论是否取自前置终点，范围内都必须存在对应类型站点（链式起点也落在范围内）
                var sb = tt.Start?.StationTypeBits ?? 0;
                if (sb != 0 && !HasType(sb))
                    errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）：起点类型「{BitsName(sb)}」在选点范围内无匹配站点");

                // 起点取自前置终点：与其前置步骤的终点类型比较（类型需有交集，否则衔接必失败）
                if (step.UsePickedStart && sb != 0 && prevTt != null)
                {
                    var prevEnd = prevTt.End?.StationTypeBits ?? 0;
                    if (prevEnd != 0 && (prevEnd & sb) == 0)
                        errors.Add($"模板「{tpl.Name}」步骤{i + 1}（{tt.Label}）起点类型「{BitsName(sb)}」与步骤{i}（{prevTt.Label}）终点类型「{BitsName(prevEnd)}」不匹配（起点取自前置终点）");
                }

                prevTt = tt;
            }
        }
        return errors;
    }

    /// <summary>任务模板 Value → 显示名（未找到回退原值），用于步骤开始日志。</summary>
    private string TaskTemplateLabel(string value)
    {
        var t = _taskTemplates.GetAll().FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
        return t?.Label ?? value;
    }

    /// <summary>执行一步任务模板：选点（与加锁原子化，杜绝并发选中同一点）、组装任务、经模块下发。返回下发的任务号（失败返回 null）。</summary>
private async Task<string?> RunTemplateStep(AutoStepDto step, ExecCtx ctx, WcsSettingsDto settings, string roundId, string stepNo, ConcurrentBag<string> taskIds, ConcurrentDictionary<string, string> taskDetails)
    {
        var tpl = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
        if (tpl == null) { _log.Add(roundId, $"步骤 {stepNo} 任务模板缺失：{step.TemplateValue}", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"任务模板缺失：{step.TemplateValue}", "#f87171"); return null; }
        // 选点范围约束：库存挑选已按范围过滤；起点/终点选点也必须在范围内。
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
            : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null
            ? allStations
            : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();

var usePickedStart = step.UsePickedStart;
        var hasPick = !string.IsNullOrEmpty(ctx.ContainerCode);

string? container;

        // 容器：优先引用指定前置步骤（PickedStepIndex>0）；其次旧逻辑最近前置挑选（-1 或旧字段 UsePickedContainer）；
        // 最后按模板前缀自动生成（0）。
        if (step.PickedStepIndex > 0)
        {
            if (!ctx.PickedByStep.TryGetValue(step.PickedStepIndex, out var c) || string.IsNullOrEmpty(c))
            {
                _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 容器引用第 {step.PickedStepIndex} 步挑选的容器，但该步未产生容器号，跳过", "#f87171");
                _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：容器引用第 {step.PickedStepIndex} 步容器不可用，跳过", "#f87171");
                return null;
            }
            container = c;
            ctx.ContainerCode = c;
        }
        else if (step.PickedStepIndex == -1 || (step.PickedStepIndex == 0 && step.UsePickedContainer))
        {
            if (!hasPick) { _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 容器使用前置挑选，但无可用托盘/货物，跳过", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：容器使用前置挑选但无可用托盘/货物，跳过", "#f87171"); return null; }
            container = ctx.ContainerCode;
        }
        else if (tpl.NeedsContainer)
        {
            container = GenerateContainerCode(tpl.ContainerPrefix);
            ctx.ContainerCode = container; // 生成的容器作为后续「使用前置容器」的来源
            _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 自动生成容器：{container}", "#38bdf8");
        }
        else
        {
            container = "";
        }

        // 起点：取自前置步骤终点（链路衔接）or 按模板起点类型在范围内选点；
        // 终点：排除占用站/全部锁定站/本次起点。选点与加锁在 _chainLock 临界区内原子完成：
        // 多模板并发串行化「合并占用 → 选点 → 加锁」，后到的必然看到先到者已加的锁，杜绝竞态选中同一点。
        string? startMark = null;
        MapStationLite? dest = null;
        string? taskId = null;
        lock (_chainLock)
        {
// 每步重建最新占用集合（缓存权威；任务完成时已 RefreshNowAsync，货入位/移走即刻反映）
            var occ = BuildOccupiedFromLedger();

            if (usePickedStart)
            {
                if (string.IsNullOrEmpty(ctx.LastEndMark)) { _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点使用前置终点，但无前置终点可用，跳过", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点使用前置终点但无前置终点可用，跳过", "#f87171"); return null; }
                startMark = ctx.LastEndMark;
            }
            else
            {
                var startBits = tpl.Start?.StationTypeBits ?? 0;
                if (startBits == 0)
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 未配置起点站点类型，无法自动选点，跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：未配置起点站点类型，无法自动选点，跳过", "#f87171");
                    return null;
                }
                // 排除已被其它任务锁定的站点（起点锁 + 终点锁），避免重复选点（真正去重）
                var lockedStarts = _locks.GetLocked(_stage);
                var startPool = stations.Where(x => (x.StationType & startBits) != 0 && !lockedStarts.Contains(x.Mark)).ToList();
                var s = startPool.Count == 0 ? null : startPool[Random.Shared.Next(startPool.Count)];
                if (s == null) { _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点范围内无可匹配站点（需 {BitsName(startBits)}），跳过", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点范围内无可匹配站点（需 {BitsName(startBits)}），跳过", "#f87171"); return null; }
                startMark = s.Mark;
            }

            // 起点站点类型约束校验（usePicked 时确保库存站符合模板起点类型）
            var startBitsChk = tpl.Start?.StationTypeBits ?? 0;
            if (startBitsChk != 0 && !string.IsNullOrEmpty(startMark))
            {
                var st = stations.FirstOrDefault(s => string.Equals(s.Mark, startMark, StringComparison.OrdinalIgnoreCase));
                if (st == null || (st.StationType & startBitsChk) == 0)
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点站点类型不匹配（{startMark} 需 {BitsName(startBitsChk)}），跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点站点类型不匹配（{startMark} 需 {BitsName(startBitsChk)}），跳过", "#f87171");
                    return null;
                }
            }

            var lockedAll = _locks.GetLocked(_stage);
            dest = ChooseDestination(tpl, stations, occ, lockedAll, startMark);
            if (dest == null)
            {
                var endBits = tpl.End?.StationTypeBits ?? 0;
if (endBits != 0 && stations.Any(s => (s.StationType & endBits) != 0)
                    && !stations.Any(s => (s.StationType & endBits) != 0 && !occ.Contains(s.Mark)
                        && ((s.StationType & MapStationTypeBits.StorageLocation) == 0 || !lockedAll.Contains(s.Mark))))
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 终点范围内匹配站点均被占用（需 {BitsName(endBits)}），跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：终点范围内匹配站点均被占用（需 {BitsName(endBits)}），跳过", "#f87171");
                }
                else
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 终点范围内无可匹配站点（需 {BitsName(endBits)}），跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：终点范围内无可匹配站点（需 {BitsName(endBits)}），跳过", "#f87171");
                }
                return null;
            }

// 选点完成：同临界区内立即加锁，其他模板选点必然看到，不会重复选中
            taskId = "Auto_" + Guid.NewGuid().ToString("N")[..12];
            if (!string.IsNullOrEmpty(startMark)) { _locks.Acquire(startMark, taskId); _lockByTask[taskId] = startMark; }
            // 终点锁仅储位执行（储位同一时间只能一辆车取/放，必须独占）；
            // 接驳位允许多个任务并发前往（GRCS 排队），不加锁、不排除锁定
            if (dest != null && !string.IsNullOrEmpty(dest.Mark) && (dest.StationType & MapStationTypeBits.StorageLocation) != 0)
            { _locks.Acquire(dest.Mark, taskId); _endLockByTask[taskId] = dest.Mark; }
        }

        var startWcs = WcsOf(startMark, stations) ?? startMark ?? "";
        var destWcs = dest!.ToWcsCode();
        var mctx = new ModuleRunService.ModuleCtx
        {
            Start = startWcs,
            End = destWcs,
            Container = container,
            Warehouse = settings.SceneName,
            TaskType = tpl.Value,
            TaskId = taskId,
        };

        var group = new WcsTaskGroup
        {
            GroupId = "G_" + Guid.NewGuid().ToString("N")[..10],
            MsgTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            PriorityCode = 5,
            Warehouse = settings.SceneName,
            Tasks = [new WcsTaskItem
            {
                TaskId = taskId,
                TaskType = tpl.Value,
                ContainerCode = container,
                StationCode = [startWcs, destWcs],
                AreaCode = [],
            }],
        };

// 强制结束后遗留轮次不得继续下发/写回锁定：释放本次占用的容器后退出（仅「停止轮询」不影响已启动轮次的链式步骤）
        if (!_running && _halted)
        {
            _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 已强制结束，跳过下发（{startWcs}→{destWcs}）", "#fbbf24");
            var pc = new List<string> { container ?? "" };
            if (!string.IsNullOrEmpty(ctx.PalletCode) && !string.Equals(ctx.PalletCode, container, StringComparison.OrdinalIgnoreCase)) pc.Add(ctx.PalletCode);
            _invStore.RecyclePicked(pc);   // 强制结束：本次选中的容器放回账本
            return null;
        }

        // 统一经 ModuleRunService：起点模块(下发前) → 下发 → 起点之后模块(下发成功后)
        var (ok, code, json) = await _modules.SendTaskWithModulesAsync(group, roundId);
        // 任务占用的容器号（主容器号：托盘或货物；带货托补记托盘号，与主容器号不同才追加）
        var taskContainers = new List<string> { container ?? "" };
        if (!string.IsNullOrEmpty(ctx.PalletCode) && !string.Equals(ctx.PalletCode, container, StringComparison.OrdinalIgnoreCase))
            taskContainers.Add(ctx.PalletCode);
        if (ok)
        {
            Interlocked.Increment(ref _executed);
            taskIds.Add(taskId);
            taskDetails[taskId] = $"{tpl.Label} [{container}] {startWcs}->{destWcs}";
            _invStore.MarkBusy(taskContainers, taskId);   // 账本：picked → busy（持久化，FINISHED 后释放）
            _log.Add(roundId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
            // 本步终点作为后续步骤（起点取自前置终点）的前置终点
            ctx.LastEndMark = dest.Mark;
            // FINISHED 后释放起点锁与账本占用
            _ = ReleaseOnFinishAsync(taskId, dest.Mark);
            // 终点模块由自动化任务自己跑（避免 FinishedModuleWatcher 重复执行 Auto_ 任务）。
            // 有终点模块 → 强制等待 FINISHED 后跑终点模块（等其 success）；无终点模块 → 按 WaitForFinish 等 FINISHED。
            var endIds = tpl.End?.AfterModules ?? [];
            try
            {
                if (endIds.Count > 0)
                {
                    await _stage.WaitFinishedAsync(taskId);
                    await _modules.RunEndModulesAsync(taskId, mctx, roundId);
                }
                else if (step.WaitForFinish)
                {
                    await _stage.WaitFinishedAsync(taskId);
                }
            }
            catch (Exception ex)
            {
                _log.Add(roundId, $"步骤 {stepNo} 终点阶段异常 {taskId}：{ex.Message}", "#f87171");
            }
            // 等待/终点模块完成后同步释放本站点锁与账本占用（ReleaseOnFinishAsync 稍后幂等再跑一遍），
            // 保证下一步骤选点立刻看到锁已释放，避免链式衔接选点被上一任务残留锁排除
            ReleaseTaskLocks(taskId);
            _invStore.Release(taskId, dest.Mark);   // 账本：busy → idle，位置更新到终点
            return taskId;
        }
        else
        {
            _log.Add(roundId, $"步骤 {stepNo} 下发失败 {taskId}：HTTP {code} {json[..Math.Min(json.Length, 200)]}", "#f87171");
            _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：下发失败 HTTP {code} {json[..Math.Min(json.Length, 200)]}", "#f87171");
            ReleaseTaskLocks(taskId);
            // 下发失败：账本占用保留（fail 前缀），下轮快照对照 GRCS 缓存确认后回收——防「超时但 GRCS 已收」重复选
            _invStore.MarkFail(taskContainers, taskId);
        }
        return null;
    }

    private async Task ReleaseOnFinishAsync(string taskId, string destMark)
    {
        try { await _stage.WaitFinishedAsync(taskId); }
        catch
        {
            // 等待被中断（停止/强制结束时刻）：任务未必完成——账本占用保留防重复选，站点锁保守释放
            _log.Add($"⚠ 任务 {taskId} 等待 FINISHED 被中断，账本占用保留（防重复选货）", "#f59e0b");
            ReleaseTaskLocks(taskId);
            return;
        }
        ReleaseTaskLocks(taskId);
        _invStore.Release(taskId, destMark);
    }

    /// <summary>释放某个任务持有的全部站点锁（起点 + 终点）。</summary>
    private void ReleaseTaskLocks(string taskId)
    {
        if (_lockByTask.TryRemove(taskId, out var mk)) _locks.Release(mk);
        if (_endLockByTask.TryRemove(taskId, out var emk)) _locks.Release(emk);
    }

/// <summary>查询 GRCS 库存并按「以前的逻辑」分类统计 + 明细：纯空托 / 带货托 / 纯货物 / 锁定中。
    /// 纯货物 = 编码含 Cargo 且无同站点托盘；托盘 = 编码含 Container；带货托 = 同当前站点有关联货物；
    /// 锁定中 = 移动单元数（任务数 + 选点未下发单元；货+托同任务算一个）。
    /// 明细仅列出有货/托的储位（不包含空储位）。</summary>
    public async Task<InventorySummaryDto> GetInventorySummaryAsync()
    {
        var dto = new InventorySummaryDto();
        // 点击查库存 = 实时拉取 GRCS 最新库存再统计（失败静默沿用旧缓存；分类统计以 GRCS 为准，锁定统计以账本为准）
        await _inventoryCache.RefreshNowAsync();
        var settings = _settings.Get();
        if (settings == null || !_inventoryCache.Ready) return dto;
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase) : null;
        // 锁定中 = 账本占用单元数（busy 按任务去重 + picked 数；GRCS 锁定/下发失败保留不计入）
        var inv = _invStore.All();
        var busyTasks = inv.Where(r => r.Status == "busy" && !string.IsNullOrEmpty(r.TaskId)
            && !r.TaskId.StartsWith(WcsInventoryStore.FailPrefix, StringComparison.OrdinalIgnoreCase)
            && !r.TaskId.StartsWith(WcsInventoryStore.GrcsLockPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var pickedCount = inv.Count(r => r.Status == "picked");
        dto.Locked = busyTasks + pickedCount;
        var records = _inventoryCache.Records;
        // 仅统计「储位」内的库存（与选池一致，排除分拣/接驳位流转中的货）
        var stations = _map.GetStations();
        var storageMarks = new HashSet<string>(stations.Where(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0).Select(s => s.Mark), StringComparer.OrdinalIgnoreCase);
        records = records.Where(c => storageMarks.Contains(c.CurrentStationCode ?? "")).ToList();
        // 第一遍：收集 Container / Cargo 的当前站点（同站点关联判定，保证带货托与纯货物互斥）+ 站点→货物号映射
        var containerStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cargoStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cargoByStation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in records)
        {
            if (rangeSet != null && !rangeSet.Contains(c.CurrentStationCode ?? "")) continue;
            var code = c.Code ?? "";
            if (code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
            {
                var st = c.CurrentStationCode ?? "";
                cargoStations.Add(st);
                if (!string.IsNullOrEmpty(st) && !cargoByStation.ContainsKey(st)) cargoByStation[st] = code;
            }
            else if (code.Contains("Container", StringComparison.OrdinalIgnoreCase)) containerStations.Add(c.CurrentStationCode ?? "");
        }
        // 锁定明细：账本 busy（按任务合并主容器+CargoCode）与 picked（选点未下发）
        var lockedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in inv.Where(r => r.Status == "busy" && !string.IsNullOrEmpty(r.TaskId)
            && !r.TaskId.StartsWith(WcsInventoryStore.FailPrefix, StringComparison.OrdinalIgnoreCase)
            && !r.TaskId.StartsWith(WcsInventoryStore.GrcsLockPrefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase))
        {
            var rows = g.ToList();
            var pallet = rows.FirstOrDefault(r => (r.Code ?? "").Contains("Container", StringComparison.OrdinalIgnoreCase));
            var main = pallet?.Code ?? rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.Code))?.Code ?? "";
            var cargoCode = rows.FirstOrDefault(r => (r.Code ?? "").Contains("Cargo", StringComparison.OrdinalIgnoreCase))?.Code;
            if (string.IsNullOrEmpty(main)) continue;
            dto.LockedItems.Add(new InventoryDetailItem { Code = main, CargoCode = cargoCode });
            lockedCodes.Add(main);
        }
        foreach (var r in inv.Where(x => x.Status == "picked"))
        {
            dto.LockedItems.Add(new InventoryDetailItem { Code = r.Code });
            lockedCodes.Add(r.Code);
        }
        var occupiedCodes = new HashSet<string>(inv.Where(r => r.Status == "busy" || r.Status == "picked").Select(r => r.Code), StringComparer.OrdinalIgnoreCase);
        foreach (var c in records)
        {
            if (rangeSet != null && !rangeSet.Contains(c.CurrentStationCode ?? "")) continue;
            var code = c.Code ?? "";
            if (lockedCodes.Contains(code))
            {
                var item = dto.LockedItems.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
                if (item != null && string.IsNullOrEmpty(item.Station)) item.Station = c.CurrentStationCode;
            }
            bool busy = occupiedCodes.Contains(code);
            if (busy) continue; // 被任务锁定的容器不计入分类（已算在「锁定中」）
            if (code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
            {
                // 纯货物 = 所在站点没有同站点托盘的独立货物（在托盘上的货算带货托一部分，不重复计）
                if (!containerStations.Contains(c.CurrentStationCode ?? ""))
                {
                    dto.Cargo++;
                    dto.CargoItems.Add(new InventoryDetailItem { Code = code, Station = c.CurrentStationCode });
                }
            }
            else if (code.Contains("Container", StringComparison.OrdinalIgnoreCase))
            {
                // 带货托 = 同当前站点有关联货物；否则空托
                if (cargoStations.Contains(c.CurrentStationCode ?? ""))
                {
                    dto.Loaded++;
                    var cargoCode = cargoByStation.TryGetValue(c.CurrentStationCode ?? "", out var cc) ? cc : null;
                    dto.LoadedItems.Add(new InventoryDetailItem { Code = code, Station = c.CurrentStationCode, CargoCode = cargoCode });
                }
                else
                {
                    dto.Empty++;
                    dto.EmptyItems.Add(new InventoryDetailItem { Code = code, Station = c.CurrentStationCode });
                }
            }
        }
        return dto;
    }

/// <summary>从 WCS 库存账本构建本轮选池（范围过滤 + 仅储位 + 排除 busy/picked）。
    /// 上一轮下发失败的占用（fail 前缀）在此对照 GRCS 缓存确认：容器仍存在 → 放回账本；否则保持排除。</summary>
    private async Task<(List<InvItem> Empty, List<InvItem> Loaded, List<InvItem> Cargo, bool Ok,
        int Stored, int LockedStored, int LoadedStored, DateTime SnapshotAt)> SnapshotAsync(WcsSettingsDto settings, string roundId)
    {
        try
        {
            if (_invStore.HasFailed() && _inventoryCache.Ready)
            {
                var records = await _inventoryCache.SyncAllAsync();
                _invStore.RecycleFailed(records.Select(r => r.Code ?? "").Where(c => c != ""));
            }
            var range = _range.Get();
            var rangeSet = (range.Enabled && range.Marks.Count > 0)
                ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
                : null;
            var (empty, loaded, cargo, stored, lockedStored, loadedStored) =
                _invStore.BuildPool(rangeSet, _invStore.StorageMarks());
            return (empty, loaded, cargo, true, stored, lockedStored, loadedStored, DateTime.Now);
        }
        catch (Exception ex)
        {
            _log.Add(roundId, $"库存查询异常：{ex.Message}", "#f87171");
            return (new List<InvItem>(), new List<InvItem>(), new List<InvItem>(), false, 0, 0, 0, DateTime.MinValue);
        }
    }

    private static string? WcsOf(string? mark, List<MapStationLite> stations)
    {
        if (string.IsNullOrEmpty(mark)) return null;
        var st = stations.FirstOrDefault(s => string.Equals(s.Mark, mark, StringComparison.OrdinalIgnoreCase));
        return st?.ToWcsCode() ?? mark;
    }

    /// <summary>
    /// 选终点：优先用模板 End.StationTypeBits 指定的站点类型（唯一权威来源）；
    /// 未配置类型约束时回退到旧的「按 Value/Category/Label 关键词猜测」逻辑。
    /// 排除：占用站（有货）、全部锁定站（其他任务正在使用）、本次起点（防同任务起终同点）。
    /// 配置了类型却无匹配站点时返回 null（调用方记录错误并跳过）。
    /// </summary>
    private static MapStationLite? ChooseDestination(TaskTemplateDto tpl, List<MapStationLite> stations, HashSet<string> occupied, HashSet<string> lockedStations, string? excludeMark)
    {
bool Free(MapStationLite s)
            => !occupied.Contains(s.Mark)
            // 储位同一时间只能一辆车取/放：排除进行中任务锁定的储位；接驳位允许多任务并发（GRCS 排队），不排除锁定
            && ((s.StationType & MapStationTypeBits.StorageLocation) == 0 || !lockedStations.Contains(s.Mark))
            && (excludeMark == null || !string.Equals(s.Mark, excludeMark, StringComparison.OrdinalIgnoreCase));

        // 范围内「类型匹配且未被占用/未锁定/非同点」的站点里随机选一个（不再固定取第一个）
        MapStationLite? RandomPick(Func<MapStationLite, bool> pred)
        {
            var pool = stations.Where(pred).ToList();
            return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
        }
        var bits = tpl.End?.StationTypeBits ?? 0;
        if (bits != 0)
            return RandomPick(s => (s.StationType & bits) != 0 && Free(s));

        // ── 未配置终点类型时的旧关键词兜底（兼容历史模板）──
        var v = $"{tpl.Value} {tpl.Category} {tpl.Label}".ToLowerInvariant();
        if (v.Contains("sort") || v.Contains("分拣"))
            return RandomPick(s => (s.StationType & MapStationTypeBits.PickingStation) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && Free(s));
        if (v.Contains("inbound") || v.Contains("入库"))
            return RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && Free(s));
        return RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && Free(s))
            ?? RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && Free(s))
            ?? RandomPick(s => (s.StationType & MapStationTypeBits.PickingStation) != 0 && Free(s));
    }

    /// <summary>按前缀生成容器号（前缀留空默认 Container），用于不使用前置挑选的自动化步骤（如入库段2）。</summary>
    private static string GenerateContainerCode(string prefix)
    {
var p = string.IsNullOrWhiteSpace(prefix) ? "Container" : prefix.Trim();
        return p + DateTime.Now.ToString("HHmmssfff") + Random.Shared.Next(10, 99);
    }

    /// <summary>把站点类型位解码为中文名（如 12 → "储位+接驳位"），用于日志展示。</summary>
    private static string BitsName(int bits)
    {
        if (bits == 0) return "不限";
        var names = new List<string>();
        if ((bits & MapStationTypeBits.NormalRoad) != 0) names.Add("普通路");
        if ((bits & MapStationTypeBits.HighWay) != 0) names.Add("高速路");
        if ((bits & MapStationTypeBits.PeopleStation) != 0) names.Add("人工位");
        if ((bits & MapStationTypeBits.StorageLocation) != 0) names.Add("储位");
        if ((bits & MapStationTypeBits.TransferPoint) != 0) names.Add("接驳位");
        if ((bits & MapStationTypeBits.Parking) != 0) names.Add("停车");
        if ((bits & MapStationTypeBits.Charging) != 0) names.Add("充电");
        if ((bits & MapStationTypeBits.PickingStation) != 0) names.Add("分拣台");
        if ((bits & MapStationTypeBits.PeopleStation) != 0) names.Add("人工台");
        if ((bits & MapStationTypeBits.Elevator) != 0) names.Add("电梯");
        if ((bits & MapStationTypeBits.Other) != 0) names.Add("其他");
        return names.Count == 0 ? $"未知({bits})" : string.Join("+", names);
    }

    /// <summary>从库存账本重建当前储位占用集合（账本行位置 = 容器当前储位；busy 在途行位置仍为起点储位）。
    /// 注意：GRCS 侧模拟入库/移库不会自动进账本，需点「同步」后账本才反映。</summary>
    private HashSet<string> BuildOccupiedFromLedger()
    {
        var storageMarks = new HashSet<string>(
            _map.GetStations().Where(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0).Select(s => s.Mark),
            StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _invStore.All())
        {
            var st = r.Station ?? "";
            if (string.IsNullOrEmpty(st) || !storageMarks.Contains(st)) continue;
            set.Add(st);
        }
        return set;
    }

private class ExecCtx
    {
        public string? PalletCode;
        public string? PalletMark;
        public string? CargoCode;
        public string? CargoMark;
        // 最近一步确定的容器（选托盘/选货物/自动生成容器），供后续「使用前置容器」取用。
        public string? ContainerCode;
        // 每步执行后确定的容器号（步骤序号 1 起）：选托盘=托盘号、选货物=货物号、带货托=货物号、RunTemplate=该步最终容器号。
        // 供后续步骤「引用第 N 步容器」（PickedStepIndex）取用。
        public Dictionary<int, string> PickedByStep = new();
        // 上一步的终点（选托盘/选货物时=库存所在站；RunTemplate 成功后=该步终点），供后续步骤「起点取自前置终点」使用。
        public string? LastEndMark;
    }
}

public class AutoTemplateStatusDto
{
    public bool Running { get; set; }
    public int Interval { get; set; }
    public string? ActiveTemplateId { get; set; }
    public string? ActiveTemplateName { get; set; }
    public string? ActiveTabId { get; set; }
    public int Executed { get; set; }
    public string Status { get; set; } = "";
    public bool GateAuto { get; set; }
public bool AnyRunning { get; set; }
}
