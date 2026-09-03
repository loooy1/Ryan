using System.Collections.Concurrent;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Contracts.Dtos;

namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>任务下发器：选点/加锁/下发/释放，持有站点锁状态与下发临界区。</summary>
public class TaskDispatcher
{
    private readonly StationLockStore _locks;
    private readonly ITaskStageService _stage;
    private readonly ModuleRunService _modules;
    private readonly WcsInventoryStore _invStore;
    private readonly AutomationLogService _log;
    private readonly RangeConfigService _range;
    private readonly MapStoreService _map;
    private readonly TaskTemplateStore _taskTemplates;

    private readonly object _chainLock = new();
    private readonly ConcurrentDictionary<string, string> _lockByTask = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _endLockByTask = new(StringComparer.OrdinalIgnoreCase);

    public TaskDispatcher(StationLockStore locks, ITaskStageService stage, ModuleRunService modules,
        WcsInventoryStore invStore, AutomationLogService log, RangeConfigService range,
        MapStoreService map, TaskTemplateStore taskTemplates)
    {
        _locks = locks; _stage = stage; _modules = modules; _invStore = invStore;
        _log = log; _range = range; _map = map; _taskTemplates = taskTemplates;
    }

    /// <summary>释放全部站点锁（强制结束时调用）。</summary>
    public void ReleaseAllLocks()
    {
        foreach (var kv in _lockByTask) { try { _locks.Release(kv.Value); } catch { } }
        foreach (var kv in _endLockByTask) { try { _locks.Release(kv.Value); } catch { } }
        _lockByTask.Clear();
        _endLockByTask.Clear();
    }

    /// <summary>执行一步任务模板：选点（与加锁原子化）、组装任务、经模块下发。</summary>
    public async Task<string?> RunTemplateStep(AutoStepDto step, ExecCtx ctx, WcsSettingsDto settings,
        string roundId, string stepNo, ConcurrentBag<string> taskIds, ConcurrentDictionary<string, string> taskDetails,
        bool running, bool halted, InventoryCoordinator invCoord)
    {
        var tpl = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
        if (tpl == null) { _log.Add(roundId, $"步骤 {stepNo} 任务模板缺失：{step.TemplateValue}", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"任务模板缺失：{step.TemplateValue}", "#f87171"); return null; }
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
            : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null ? allStations : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();

        var usePickedStart = step.UsePickedStart;
        var hasPick = !string.IsNullOrEmpty(ctx.ContainerCode);
        string? container;

        if (step.PickedStepIndex > 0)
        {
            if (!ctx.PickedByStep.TryGetValue(step.PickedStepIndex, out var c) || string.IsNullOrEmpty(c))
            {
                _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 容器引用第 {step.PickedStepIndex} 步挑选的容器，但该步未产生容器号，跳过", "#f87171");
                _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：容器引用第 {step.PickedStepIndex} 步容器不可用，跳过", "#f87171");
                return null;
            }
            container = c; ctx.ContainerCode = c;
        }
        else if (step.PickedStepIndex == -1 || (step.PickedStepIndex == 0 && step.UsePickedContainer))
        {
            if (!hasPick) { _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 容器使用前置挑选，但无可用托盘/货物，跳过", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：容器使用前置挑选但无可用托盘/货物，跳过", "#f87171"); return null; }
            container = ctx.ContainerCode;
        }
        else if (tpl.NeedsContainer)
        {
            container = GenerateContainerCode(tpl.ContainerPrefix);
            ctx.ContainerCode = container;
            _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 自动生成容器：{container}", "#38bdf8");
        }
        else
        {
            container = "";
        }

        string? startMark = null;
        MapStationLite? dest = null;
        string? taskId = null;
        lock (_chainLock)
        {
            var occ = invCoord.BuildOccupied();

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
                var lockedStarts = _locks.GetLocked(_stage);
                var startPool = stations.Where(x => (x.StationType & startBits) != 0 && !lockedStarts.Contains(x.Mark)).ToList();
                var s = startPool.Count == 0 ? null : startPool[Random.Shared.Next(startPool.Count)];
                if (s == null) { _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点范围内无可匹配站点（需 {StationTypeHelper.BitsName(startBits)}），跳过", "#f87171"); _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点范围内无可匹配站点（需 {StationTypeHelper.BitsName(startBits)}），跳过", "#f87171"); return null; }
                startMark = s.Mark;
            }

            var startBitsChk = tpl.Start?.StationTypeBits ?? 0;
            if (startBitsChk != 0 && !string.IsNullOrEmpty(startMark))
            {
                var st = stations.FirstOrDefault(s => string.Equals(s.Mark, startMark, StringComparison.OrdinalIgnoreCase));
                if (st == null || (st.StationType & startBitsChk) == 0)
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点站点类型不匹配（{startMark} 需 {StationTypeHelper.BitsName(startBitsChk)}），跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点站点类型不匹配（{startMark} 需 {StationTypeHelper.BitsName(startBitsChk)}），跳过", "#f87171");
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
                        && ((s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) == 0 || !lockedAll.Contains(s.Mark))))
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 终点范围内匹配站点均被占用（需 {StationTypeHelper.BitsName(endBits)}），跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：终点范围内匹配站点均被占用（需 {StationTypeHelper.BitsName(endBits)}），跳过", "#f87171");
                }
                else
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 终点范围内无可匹配站点（需 {StationTypeHelper.BitsName(endBits)}），跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：终点范围内无可匹配站点（需 {StationTypeHelper.BitsName(endBits)}），跳过", "#f87171");
                }
                return null;
            }

            taskId = "Auto_" + Guid.NewGuid().ToString("N")[..12];
            if (!string.IsNullOrEmpty(startMark)) { _locks.Acquire(startMark, taskId); _lockByTask[taskId] = startMark; }
            if (dest != null && !string.IsNullOrEmpty(dest.Mark) && (dest.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0)
            { _locks.Acquire(dest.Mark, taskId); _endLockByTask[taskId] = dest.Mark; }
        }

        var startWcs = WcsOf(startMark, stations) ?? startMark ?? "";
        var destWcs = dest!.ToWcsCode();
        var mctx = new ModuleRunService.ModuleCtx
        {
            Start = startWcs, End = destWcs, Container = container,
            Warehouse = settings.SceneName, TaskType = tpl.Value, TaskId = taskId,
        };

        var group = new WcsTaskGroup
        {
            GroupId = "G_" + Guid.NewGuid().ToString("N")[..10],
            MsgTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            PriorityCode = 5,
            Warehouse = settings.SceneName,
            Tasks = [new WcsTaskItem
            {
                TaskId = taskId, TaskType = tpl.Value, ContainerCode = container,
                StationCode = [startWcs, destWcs], AreaCode = [],
            }],
        };

        if (!running && halted)
        {
            _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 已强制结束，跳过下发（{startWcs}→{destWcs}）", "#fbbf24");
            var pc = new List<string> { container ?? "" };
            if (!string.IsNullOrEmpty(ctx.PalletCode) && !string.Equals(ctx.PalletCode, container, StringComparison.OrdinalIgnoreCase)) pc.Add(ctx.PalletCode);
            _invStore.RecyclePicked(pc);
            return null;
        }

        var (ok, code, json) = await _modules.SendTaskWithModulesAsync(group, roundId);
        var taskContainers = new List<string> { container ?? "" };
        if (!string.IsNullOrEmpty(ctx.PalletCode) && !string.Equals(ctx.PalletCode, container, StringComparison.OrdinalIgnoreCase))
            taskContainers.Add(ctx.PalletCode);
        if (ok)
        {
            taskIds.Add(taskId);
            taskDetails[taskId] = $"{tpl.Label} [{container}] {startWcs}->{destWcs}";
            _invStore.MarkBusy(taskContainers, taskId);
            _log.Add(roundId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
            ctx.LastEndMark = dest.Mark;
            _ = ReleaseOnFinishAsync(taskId, dest.Mark);
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
            ReleaseTaskLocks(taskId);
            _invStore.Release(taskId, dest.Mark);
            return taskId;
        }
        else
        {
            _log.Add(roundId, $"步骤 {stepNo} 下发失败 {taskId}：HTTP {code} {json[..Math.Min(json.Length, 200)]}", "#f87171");
            _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：下发失败 HTTP {code} {json[..Math.Min(json.Length, 200)]}", "#f87171");
            ReleaseTaskLocks(taskId);
            _invStore.MarkFail(taskContainers, taskId);
        }
        return null;
    }

    private async Task ReleaseOnFinishAsync(string taskId, string destMark)
    {
        try { await _stage.WaitFinishedAsync(taskId); }
        catch
        {
            _log.Add($"⚠ 任务 {taskId} 等待 FINISHED 被中断，账本占用保留（防重复选货）", "#f59e0b");
            ReleaseTaskLocks(taskId);
            return;
        }
        ReleaseTaskLocks(taskId);
        _invStore.Release(taskId, destMark);
    }

    private void ReleaseTaskLocks(string taskId)
    {
        if (_lockByTask.TryRemove(taskId, out var mk)) _locks.Release(mk);
        if (_endLockByTask.TryRemove(taskId, out var emk)) _locks.Release(emk);
    }

    private static MapStationLite? ChooseDestination(TaskTemplateDto tpl, List<MapStationLite> stations, HashSet<string> occupied, HashSet<string> lockedStations, string? excludeMark)
    {
        bool Free(MapStationLite s)
            => !occupied.Contains(s.Mark)
            && ((s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) == 0 || !lockedStations.Contains(s.Mark))
            && (excludeMark == null || !string.Equals(s.Mark, excludeMark, StringComparison.OrdinalIgnoreCase));

        MapStationLite? RandomPick(Func<MapStationLite, bool> pred)
        {
            var pool = stations.Where(pred).ToList();
            return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
        }
        var bits = tpl.End?.StationTypeBits ?? 0;
        if (bits != 0)
            return RandomPick(s => (s.StationType & bits) != 0 && Free(s));

        var v = $"{tpl.Value} {tpl.Category} {tpl.Label}".ToLowerInvariant();
        if (v.Contains("sort") || v.Contains("分拣"))
            return RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.PickingStation) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.TransferPoint) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0 && Free(s));
        if (v.Contains("inbound") || v.Contains("入库"))
            return RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.TransferPoint) != 0 && Free(s));
        return RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.TransferPoint) != 0 && Free(s))
            ?? RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0 && Free(s))
            ?? RandomPick(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.PickingStation) != 0 && Free(s));
    }

    private static string GenerateContainerCode(string prefix)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? "Container" : prefix.Trim();
        return p + DateTime.Now.ToString("HHmmssfff") + Random.Shared.Next(10, 99);
    }

    private static string? WcsOf(string? mark, List<MapStationLite> stations)
    {
        if (string.IsNullOrEmpty(mark)) return null;
        var st = stations.FirstOrDefault(s => string.Equals(s.Mark, mark, StringComparison.OrdinalIgnoreCase));
        return st?.ToWcsCode() ?? mark;
    }

    /// <summary>单模板执行上下文（步骤链状态容器）。</summary>
    public class ExecCtx
    {
        public string? PalletCode;
        public string? PalletMark;
        public string? CargoCode;
        public string? CargoMark;
        public string? ContainerCode;
        public Dictionary<int, string> PickedByStep = new();
        public string? LastEndMark;
    }
}