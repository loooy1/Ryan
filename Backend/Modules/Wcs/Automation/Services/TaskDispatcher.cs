using System.Collections.Concurrent;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>任务下发器：按数据库储位状态选点、下发任务并登记完成收尾。</summary>
public class TaskDispatcher
{
    private readonly ITaskStageService _stage;
    private readonly ModuleRunService _modules;
    private readonly WcsInventoryStore _invStore;
    private readonly AutomationLogService _log;
    private readonly RangeConfigService _range;
    private readonly MapStoreService _map;
    private readonly TaskTemplateStore _taskTemplates;
    private readonly TaskCompletionCoordinator _completion;

    private readonly object _chainLock = new();

    public TaskDispatcher(ITaskStageService stage, ModuleRunService modules,
        WcsInventoryStore invStore, AutomationLogService log, RangeConfigService range,
        MapStoreService map, TaskTemplateStore taskTemplates, TaskCompletionCoordinator completion)
    {
        _stage = stage; _modules = modules; _invStore = invStore;
        _log = log; _range = range; _map = map; _taskTemplates = taskTemplates;
        _completion = completion;
    }

    /// <summary>强制结束时撤销本进程内的任务登记。</summary>
    public void ReleaseAllReservations()
    {
        _completion.ReleaseAllReservations();
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
        // 输入容器来自模板生成或前置步骤；起点库存只决定最终发送给 RCS 的编号，
        // 不得覆盖这个输入值。
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
        string? preparedInsertedCode = null;
        lock (_chainLock)
        {
            var occ = invCoord.BuildOccupied();

            if (usePickedStart)
            {
                if (!string.IsNullOrWhiteSpace(ctx.LastTaskId))
                {
                    var completed = _stage.GetAll().LastOrDefault(record =>
                        string.Equals(record.TaskId, ctx.LastTaskId, StringComparison.OrdinalIgnoreCase)
                        && record.Stage == "WCS_COMPLETED" && record.IsSuccess);
                    if (completed == null)
                    {
                        _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点使用前置终点，但上一任务尚未 WCS 任务完成，跳过", "#f87171");
                        _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：上一任务未完成 WCS 收尾，不能使用前置终点", "#f87171");
                        return null;
                    }
                    startMark = ToMapMark(completed.EndStationCode);
                }
                else
                {
                    // 本轮刚挑选的库存尚未形成前置任务，只借用挑选站点定位，库存内容仍从数据库读取。
                    startMark = ctx.CargoMark ?? ctx.PalletMark;
                }
                if (string.IsNullOrWhiteSpace(startMark))
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 未取得前置起点站点，跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：未取得前置起点站点", "#f87171");
                    return null;
                }
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
                var startAvailableStorage = _invStore.GetStartAvailableStorageMarks();
                var startPool = stations.Where(x => (x.StationType & startBits) != 0
                    && ((x.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) == 0
                        || startAvailableStorage.Contains(x.Mark))).ToList();
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

            dest = ChooseDestination(tpl, stations, occ, startMark);
            if (dest == null)
            {
                var endBits = tpl.End?.StationTypeBits ?? 0;
                if (endBits != 0 && stations.Any(s => (s.StationType & endBits) != 0)
                    && !stations.Any(s => (s.StationType & endBits) != 0 && !occ.Contains(s.Mark)))
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

            var startIsStorage = !string.IsNullOrEmpty(startMark)
                && (stations.FirstOrDefault(s => string.Equals(s.Mark, startMark, StringComparison.OrdinalIgnoreCase))?.StationType
                    & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0;

            taskId = "Auto_" + Guid.NewGuid().ToString("N")[..12];
            string? startStorageTaskLockMark = null;
            if (startIsStorage)
            {
                if (!_invStore.TryLockStorageForTask(taskId, startMark!, usePickedStart))
                {
                    _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点储位 {startMark} 当前数据库状态不可作为起点，跳过", "#f87171");
                    _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点储位 {startMark} 当前数据库状态不可作为起点", "#f87171");
                    return null;
                }
                startStorageTaskLockMark = startMark;
            }

            var destinationIsStorage = (dest.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0;
            if (destinationIsStorage && !_invStore.TryLockDestinationForTask(taskId, dest.Mark))
            {
                if (startStorageTaskLockMark != null) _invStore.ClearTaskLock(taskId, startStorageTaskLockMark);
                _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 终点储位 {dest.Mark} 已被占用，跳过", "#f87171");
                _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：终点储位 {dest.Mark} 已被其他任务或库存占用", "#f87171");
                return null;
            }
            var preparedInserted = false;
            if (!_invStore.TryPrepareTaskStartContainer(startMark!, container, out preparedInserted, out var prepareError))
            {
                if (destinationIsStorage) _invStore.ClearTaskLock(taskId, dest.Mark);
                if (startStorageTaskLockMark != null) _invStore.ClearTaskLock(taskId, startStorageTaskLockMark);
                _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点容器准备失败：{prepareError}", "#f87171");
                _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点容器准备失败：{prepareError}", "#f87171");
                return null;
            }
            if (preparedInserted) preparedInsertedCode = container;

            // 预写后重新从数据库读取起点：只有托盘时下发托盘；存在货物时下发货物。
            var sourceSlot = _invStore.FindSlot(startMark!);
            var dispatchContainer = sourceSlot?.CargoCode ?? sourceSlot?.PalletCode;
            if (string.IsNullOrWhiteSpace(dispatchContainer))
            {
                if (preparedInserted) _invStore.RollbackPreparedTaskStartContainer(startMark!, container);
                if (destinationIsStorage) _invStore.ClearTaskLock(taskId, dest.Mark);
                if (startStorageTaskLockMark != null) _invStore.ClearTaskLock(taskId, startStorageTaskLockMark);
                _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 起点 {startMark} 无可搬运库存，跳过", "#f87171");
                _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：起点 {startMark} 无可搬运库存", "#f87171");
                return null;
            }

            ctx.PalletCode = string.IsNullOrWhiteSpace(sourceSlot!.PalletCode) ? null : sourceSlot.PalletCode;
            ctx.CargoCode = string.IsNullOrWhiteSpace(sourceSlot.CargoCode) ? null : sourceSlot.CargoCode;
            ctx.ContainerCode = dispatchContainer;
            container = dispatchContainer;

            try
            {
                _completion.RegisterReservation(taskId, startStorageTaskLockMark, dest.Mark);
            }
            catch
            {
                if (destinationIsStorage) _invStore.ClearTaskLock(taskId, dest.Mark);
                if (startStorageTaskLockMark != null) _invStore.ClearTaskLock(taskId, startStorageTaskLockMark);
                throw;
            }
        }

        var startWcs = WcsOf(startMark, stations) ?? startMark ?? "";
        var destWcs = dest!.ToWcsCode();
        var group = new WcsTaskGroup
        {
            GroupId = "G_" + Guid.NewGuid().ToString("N")[..10],
            MsgTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            PriorityCode = 5,
            Warehouse = settings.SceneName,
            Tasks = [new WcsTaskItem
            {
                TaskId = taskId, TaskType = tpl.Value, ContainerCode = container,
                PalletCode = ctx.PalletCode
                    ?? (IsCargoCode(container) ? "" : container ?? ""),
                CargoCode = ctx.CargoCode
                    ?? (IsCargoCode(container) ? container ?? "" : ""),
                StationCode = [startWcs, destWcs], AreaCode = [],
            }],
        };
        var taskUnits = new[] { group.Tasks[0].PalletCode, group.Tasks[0].CargoCode }
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!running && halted)
        {
            _log.Add(roundId, $"步骤 {stepNo} 模板[{tpl.Label}] 已强制结束，跳过下发（{startWcs}→{destWcs}）", "#fbbf24");
            _completion.CancelReservation(taskId);
            if (preparedInsertedCode != null)
                _invStore.RollbackPreparedTaskStartContainer(startMark!, preparedInsertedCode);
            _invStore.RecyclePicked(taskUnits.Where(code => !string.Equals(code, preparedInsertedCode, StringComparison.OrdinalIgnoreCase)));
            return null;
        }

        (bool ok, int code, string json) dispatch;
        try
        {
            dispatch = await _modules.SendTaskWithModulesAsync(group, roundId);
        }
        catch
        {
            _completion.CancelReservation(taskId);
            throw;
        }
        var (ok, code, json) = dispatch;
        if (ok)
        {
            taskIds.Add(taskId);
            taskDetails[taskId] = $"{tpl.Label} [{container}] {startWcs}->{destWcs}";
            _invStore.MarkBusy(taskUnits);
            _completion.Activate(taskId);
            _log.Add(roundId, $"✓ 步骤 {stepNo} 完成！", "#4ade80");
            ctx.LastTaskId = taskId;
            var endIds = tpl.End?.AfterModules ?? [];
            try
            {
                if (endIds.Count > 0 || step.WaitForFinish)
                {
                    if (!await _stage.WaitWcsCompletedAsync(taskId))
                    {
                        _log.Add(roundId, $"步骤 {stepNo} WCS 收尾失败：{taskId}，停止后续步骤", "#f87171");
                        _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：任务 {taskId} WCS 收尾失败", "#f87171");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Add(roundId, $"步骤 {stepNo} 终点阶段异常 {taskId}：{ex.Message}", "#f87171");
            }
            return taskId;
        }
        else
        {
            _log.Add(roundId, $"步骤 {stepNo} 下发失败 {taskId}：HTTP {code} {json[..Math.Min(json.Length, 200)]}", "#f87171");
            _log.AddOrUpdate("[模板步骤失败]", $"模板「{tpl.Label}」：下发失败 HTTP {code} {json[..Math.Min(json.Length, 200)]}", "#f87171");
            _completion.CancelReservation(taskId);
            if (code == -1)
                _invStore.MarkFail(taskUnits, taskId);
            else
            {
                if (preparedInsertedCode != null)
                    _invStore.RollbackPreparedTaskStartContainer(startMark!, preparedInsertedCode);
                _invStore.RecyclePicked(taskUnits.Where(unit => !string.Equals(unit, preparedInsertedCode, StringComparison.OrdinalIgnoreCase)));
            }
        }
        return null;
    }

    private static MapStationLite? ChooseDestination(TaskTemplateDto tpl, List<MapStationLite> stations, HashSet<string> occupied, string? excludeMark)
    {
        bool Free(MapStationLite s)
            // 人工分拣台由 RCS 队列调度，WCS 不因其下属实际分拣台已满而阻止下发。
            => (s.StationType & Contracts.Dtos.MapStationTypeBits.PeopleStation) != 0
            || (!occupied.Contains(s.Mark)
            && (excludeMark == null || !string.Equals(s.Mark, excludeMark, StringComparison.OrdinalIgnoreCase)));

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

    private static bool IsCargoCode(string? code)
        => !string.IsNullOrWhiteSpace(code)
            && code.Contains("Cargo", StringComparison.OrdinalIgnoreCase);

    private static string? WcsOf(string? mark, List<MapStationLite> stations)
    {
        if (string.IsNullOrEmpty(mark)) return null;
        var st = stations.FirstOrDefault(s => string.Equals(s.Mark, mark, StringComparison.OrdinalIgnoreCase));
        return st?.ToWcsCode() ?? mark;
    }

    private static string ToMapMark(string stationCode)
    {
        var value = stationCode?.Trim() ?? "";
        var separator = value.LastIndexOf('_');
        return separator > 0 && int.TryParse(value[(separator + 1)..], out _)
            ? value[..separator]
            : value;
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
        public string? LastTaskId;
    }
}
