using System.Collections.Concurrent;
using System.Text.Json;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using InvItem = WCSBackend.Modules.Wcs.Infrastructure.WcsInventoryStore.InvItem;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Proxy.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WCSBackend.Modules.Wcs.Automation.Services;

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
    private readonly AutomationLogService _log;
    private readonly ITaskStageService _stage;
    private readonly AutomationGate _gate;
    private readonly ModuleRunService _modules;
    private readonly TaskTemplateStore _taskTemplates;
    private readonly AutoTemplateStore _templates;
    private readonly MockRuleStore _mocks;
    private readonly WcsInventoryStore _invStore;
    private readonly TemplateValidator _validator;
    private readonly InventoryCoordinator _invCoord;
    private readonly TaskDispatcher _dispatcher;
    private readonly ILogger<AutoTemplateRunner> _logger;

    // 选点/加锁/占用合并临界区：多模板并发串行化「选点→加锁」，避免竞态选中同一点；occupied 合并计算同锁保护

    private readonly object _stateLock = new();
    private bool _running;
    private volatile bool _halted; // 强制结束（全部重置）专用：在途轮次的链式步骤立即停止下发；仅「停止轮询」不影响已启动轮次
    private int _interval = 3000;
    private bool _chainTemplates; // 模板链路衔接：顺序执行并将终点/容器传到下一模板
    private List<string> _activeTemplateIds = new();
    private int _executed;
    private int _roundSeq; // 成功轮数（选托盘/选货物成功后才 +1）
    private string _status = "未启动";
    private string? _activeTabId;
    private CancellationTokenSource? _cts;

    // 容器占用/选池已迁移到 WcsInventoryStore 账本（SQLite 持久化），不再使用内存集合。

    public AutoTemplateRunner(
        MapStoreService map, RangeConfigService range, WcsSettingsService settings,
        AutomationLogService log, ITaskStageService stage, AutomationGate gate,
        ModuleRunService modules, TaskTemplateStore taskTemplates, AutoTemplateStore templates,
        MockRuleStore mocks, WcsInventoryStore invStore,
        ILogger<AutoTemplateRunner> logger, TaskCompletionCoordinator completion)
    {
        _map = map; _range = range; _settings = settings; _log = log;
        _stage = stage; _gate = gate; _modules = modules; _taskTemplates = taskTemplates;
        _templates = templates; _mocks = mocks; _invStore = invStore;
        _logger = logger;
        _validator = new TemplateValidator(map, range, taskTemplates, templates);
        _invCoord = new InventoryCoordinator(invStore, range, log);
        _dispatcher = new TaskDispatcher(stage, modules, invStore, log, range, map, taskTemplates, completion);
    }

    // ── 状态 ──
    public bool Running => _running;
    public int Interval => _interval;
    public string? ActiveTemplateId => _activeTemplateIds.FirstOrDefault();
    public string? ActiveTemplateName => string.Join(" + ", _activeTemplateIds.Select(id => _templates.GetAll().FirstOrDefault(t => t.Id == id)?.Name).Where(n => n != null));
    public int Executed => _executed;
    public string Status => _status;
    public string? ActiveTabId => _activeTabId;

    /// <summary>模板链路衔接：开启时模板顺序执行，前一模板的终点/容器传给下一模板作为起点/容器来源。</summary>
    public bool ChainTemplates
    {
        get => _chainTemplates;
        set { lock (_stateLock) _chainTemplates = value; }
    }

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
            ChainTemplates = _chainTemplates,
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
        _dispatcher.ReleaseAllReservations();
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
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动化轮询循环异常退出");
            lock (_stateLock) { _status = "异常停机"; }
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
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "等待本轮任务完成异常：{MaskId}", parentId);
            _log.Add(parentId, $"等待任务完成异常：{ex.Message}", "#f87171");
        }
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
        var (emptyPallets, loadedPallets, cargos, snapOk, diagStored, diagLocked, diagLoaded, diagSnapAt) = await _invCoord.SnapshotAsync(roundId);
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

        async Task RefreshInventoryPools()
        {
            var snapshot = await _invCoord.SnapshotAsync(roundId);
            lock (poolLock)
            {
                emptyPallets.Clear();
                emptyPallets.AddRange(snapshot.Empty);
                loadedPallets.Clear();
                loadedPallets.AddRange(snapshot.Loaded);
                cargos.Clear();
                cargos.AddRange(snapshot.Cargo);
            }
        }

        async Task RunOne(AutoTemplateDto tpl, int k, string childId, TaskDispatcher.ExecCtx? chainCtx = null)
        {
            var name = tpl.Name;
            var ctx = chainCtx ?? new TaskDispatcher.ExecCtx();
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
                            AutoStepKinds.RunTemplate => $"\u6267\u884c\u4efb\u52a1\u6a21\u677f\u300c{TaskTemplateLabel(step.TemplateValue)}\u300d({step.TemplateValue}) \u00b7 \u8d77\u70b9={StartSourceLabel(step)} \u00b7 \u7b49\u5f85\u5b8c\u6210={(step.WaitForFinish ? "\u662f" : "\u5426")}",
                            _ => step.Kind,
                        };
                        _log.Add(childId, $"▶ 步骤 {stepNo}：{desc}", "#38bdf8");
                        if (step.Kind == AutoStepKinds.PickPallet)
                        {
                            // 每个选库存步骤使用最新的 WCS 储位状态，避免前面任务
                            // 完成后新产生的库存仍被旧快照隐藏。
                            await RefreshInventoryPools();
                            InvItem? pick;
                            lock (poolLock)
                            {
                                var pool = step.PalletFilter switch
                                {
                                    "Loaded" => loadedPallets,
                                    "Any" => emptyPallets.Concat(loadedPallets).ToList(),
                                    _ => emptyPallets,
                                };
                                _log.Add(childId, $"步骤 {stepNo} 选托盘：{step.PalletFilter} 池 {pool.Count} 个", "#38bdf8");
                                if (pool.Count == 0) { _log.Add(childId, $"步骤 {stepNo} 选托盘失败：{step.PalletFilter} 池为空，等待下轮下发｜诊断：{diagText}", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{name}」：选托盘失败（{step.PalletFilter} 池为空），等待下轮下发", "#f87171"); return; }
                                pick = pool[Random.Shared.Next(pool.Count)];
                                emptyPallets.Remove(pick); loadedPallets.Remove(pick);
                                _invStore.Pick(pick.Code);   // 账本占用（picked，持久化）
                                pickedBusy.Add(pick.Code);
                            }
                            ctx.SelectedStartMark = pick.Mark;
                            _log.Add(childId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
                            MarkFirst(childId, name);
                        }
                        else if (step.Kind == AutoStepKinds.PickCargo)
                        {
                            await RefreshInventoryPools();
                            InvItem? pick;
                            lock (poolLock)
                            {
                                if (cargos.Count == 0) { _log.Add(childId, $"步骤 {stepNo} 选货物失败：货物池为空，等待下轮下发", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{name}」：选货物失败（货物池为空），等待下轮下发", "#f87171"); return; }
                                _log.Add(childId, $"步骤 {stepNo} 选货物：货物池 {cargos.Count} 个", "#38bdf8");
                                pick = cargos[Random.Shared.Next(cargos.Count)];
                                cargos.Remove(pick);
                                _invStore.Pick(pick.Code);   // 账本占用（picked，持久化）
                                pickedBusy.Add(pick.Code);
                            }
                            ctx.SelectedStartMark = pick.Mark;
                            _log.Add(childId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
                            MarkFirst(childId, name);
                        }
                        else if (step.Kind == AutoStepKinds.PickLoadedPallet)
                        {
                            await RefreshInventoryPools();
                            InvItem? pick;
                            lock (poolLock)
                            {
                                if (loadedPallets.Count == 0) { _log.Add(childId, $"步骤 {stepNo} 选带货托失败：带货托池为空，等待下轮下发｜诊断：{diagText}", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{name}」：选带货托失败（带货托池为空），等待下轮下发", "#f87171"); return; }
                                _log.Add(childId, $"步骤 {stepNo} 选带货托：带货托池 {loadedPallets.Count} 个", "#38bdf8");
                                pick = loadedPallets[Random.Shared.Next(loadedPallets.Count)];
                                loadedPallets.Remove(pick);
                                _invStore.Pick(pick.Code);   // 账本占用（picked，持久化；托盘号 = 主容器单元）
                                if (!string.IsNullOrEmpty(pick.CargoCode)) _invStore.Pick(pick.CargoCode);
                                pickedBusy.Add(pick.Code);
                                if (!string.IsNullOrEmpty(pick.CargoCode)) pickedBusy.Add(pick.CargoCode);
                            }
                            ctx.SelectedStartMark = pick.Mark;
                            _log.Add(childId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
                            MarkFirst(childId, name);
                        }
                        else if (step.Kind == AutoStepKinds.RunTemplate)
                        {
                            var tid = await _dispatcher.RunTemplateStep(step, ctx, settings, childId, stepNo, taskIds, taskDetails, _running, _halted, _invCoord);
                            if (tid == null) return;
                            MarkFirst(childId, name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Add(childId, $"步骤 {stepNo} 执行异常：{ex.Message}", "#f87171");
                        _log.AddOrUpdate("[模板步骤失败]", $"模板「{name}」：步骤 {stepNo} 执行异常：{ex.Message}", "#f87171");
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

        string? chainLastTaskId = null;
        for (int k = 0; k < tpls.Count; k++)
        {
            var idx = k;
            var cid = _log.BeginRound(tpls[idx].Name, roundId);
            if (_chainTemplates)
            {
                // 链路模式：顺序执行，将上一模板的终点/容器注入下一模板的 ctx
                var ctx = new TaskDispatcher.ExecCtx();
                if (chainLastTaskId != null) ctx.LastTaskId = chainLastTaskId;
                await RunOne(tpls[idx], idx, cid, ctx);
                chainLastTaskId = ctx.LastTaskId;
            }
            else
            {
                // 并行模式：各模板独立 ctx，并发下发
                var tpl = tpls[idx];
                var tasks = new List<Task>();
                tasks.Add(Task.Run(() => RunOne(tpl, idx, cid)));
                await Task.WhenAll(tasks);
            }
        }
        return (taskIds.ToList(), taskDetails);
    }

    /// <summary>保存前校验：委托给 TemplateValidator。</summary>
    public List<string> ValidateTemplates(List<AutoTemplateDto> items)
        => _validator.ValidateTemplates(items);

    /// <summary>任务模板 Value → 显示名（未找到回退原值），用于步骤开始日志。</summary>
    private string TaskTemplateLabel(string value) => _validator.TaskTemplateLabel(value);

    private static string StartSourceLabel(AutoStepDto step) => step.StartSource switch
    {
        AutoStartSources.SelectedStation => "\u9009\u4e2d\u7ad9\u70b9",
        AutoStartSources.PreviousTaskEnd => "\u524d\u7f6e\u4efb\u52a1\u7ec8\u70b9",
        AutoStartSources.AutoSelect => "\u6309\u7c7b\u578b\u81ea\u52a8\u9009\u70b9",
        _ => step.UsePickedStart ? "\u65e7\u914d\u7f6e\u9009\u4e2d\u7ad9\u70b9" : "\u65e7\u914d\u7f6e\u81ea\u52a8\u9009\u70b9",
    };

    /// <summary>查询 GRCS 库存并按「以前的逻辑」分类统计 + 明细：纯空托 / 带货托 / 纯货物 / 锁定中。
    /// 纯货物 = 编码含 Cargo 且无同站点托盘；托盘 = 编码含 Container；带货托 = 同当前站点有关联货物；
    /// 锁定中 = 移动单元数（任务数 + 选点未下发单元；货+托同任务算一个）。
    /// 明细仅列出有货/托的储位（不包含空储位）。</summary>
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
    public bool ChainTemplates { get; set; }
}
