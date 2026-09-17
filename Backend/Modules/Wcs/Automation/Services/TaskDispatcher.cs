using System.Collections.Concurrent;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Automation.Services;

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

    public void ReleaseAllReservations() => _completion.ReleaseAllReservations();

    /// <summary>
    /// Dispatch one automation template step. The automation context supplies only a start
    /// station or a preceding task. The task template decides whether the unit is read from
    /// current WCS inventory or generated at the start station.
    /// </summary>
    public async Task<string?> RunTemplateStep(AutoStepDto step, ExecCtx ctx, WcsSettingsDto settings,
        string roundId, string stepNo, ConcurrentBag<string> taskIds, ConcurrentDictionary<string, string> taskDetails,
        bool running, bool halted, InventoryCoordinator invCoord)
    {
        var tpl = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, step.TemplateValue, StringComparison.OrdinalIgnoreCase));
        if (tpl == null)
        {
            Fail(roundId, tplLabel: step.TemplateValue, stepNo, "任务模板不存在");
            return null;
        }

        var range = _range.Get();
        var rangeSet = range.Enabled && range.Marks.Count > 0
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase) : null;
        var allStations = _map.GetStations();
        var stations = rangeSet == null ? allStations : allStations.Where(s => rangeSet.Contains(s.Mark)).ToList();
        var startSource = ResolveStartSource(step, ctx);

        string? startMark = null;
        MapStationLite? destination = null;
        string? taskId = null;
        string? preparedInsertedCode = null;
        string palletCode = "";
        string cargoCode = "";
        string dispatchContainer = "";
        string? startStorageTaskLockMark = null;
        var destinationIsStorage = false;

        lock (_chainLock)
        {
            var occupied = invCoord.BuildOccupied();
            startMark = ResolveStartMark(startSource, ctx, tpl, stations, roundId, stepNo);
            if (string.IsNullOrWhiteSpace(startMark)) return null;

            var startBits = tpl.Start?.StationTypeBits ?? 0;
            if (startBits != 0)
            {
                var startStation = stations.FirstOrDefault(s => string.Equals(s.Mark, startMark, StringComparison.OrdinalIgnoreCase));
                if (startStation == null || (startStation.StationType & startBits) == 0)
                {
                    Fail(roundId, tpl.Label, stepNo, $"起点站点类型不匹配：{startMark}");
                    return null;
                }
            }

            destination = ChooseDestination(tpl, stations, occupied, startMark);
            if (destination == null)
            {
                Fail(roundId, tpl.Label, stepNo, "没有可用的目标站点");
                return null;
            }

            var startIsStorage = (stations.FirstOrDefault(s => string.Equals(s.Mark, startMark, StringComparison.OrdinalIgnoreCase))?.StationType
                & MapStationTypeBits.StorageLocation) != 0;
            taskId = "Auto_" + Guid.NewGuid().ToString("N")[..12];
            if (startIsStorage)
            {
                // A selected station was deliberately marked picked. A preceding task has
                // completed its WCS lifecycle. Both are valid chain starts.
                var allowChainStart = startSource != AutoStartSources.AutoSelect;
                if (!_invStore.TryLockStorageForTask(taskId, startMark, allowChainStart))
                {
                    Fail(roundId, tpl.Label, stepNo, $"起始储位不可用：{startMark}");
                    return null;
                }
                startStorageTaskLockMark = startMark;
            }

            destinationIsStorage = (destination.StationType & MapStationTypeBits.StorageLocation) != 0;
            if (destinationIsStorage && !_invStore.TryLockDestinationForTask(taskId, destination.Mark))
            {
                ClearLocks(taskId, startStorageTaskLockMark, destination.Mark, destinationIsStorage);
                Fail(roundId, tpl.Label, stepNo, $"目标储位不可用：{destination.Mark}");
                return null;
            }

            var mode = NormalizeContainerMode(tpl.ContainerMode, tpl.NeedsContainer);

            if (mode is TaskContainerModes.GenerateCargo or TaskContainerModes.GeneratePallet)
            {
                var generated = GenerateContainerCode(tpl.ContainerPrefix, mode == TaskContainerModes.GenerateCargo);
                if (!_invStore.TryPrepareTaskStartContainer(startMark, generated, out var inserted, out var prepareError))
                {
                    ClearLocks(taskId, startStorageTaskLockMark, destination.Mark, destinationIsStorage);
                    Fail(roundId, tpl.Label, stepNo, $"起点容器准备失败：{prepareError}");
                    return null;
                }
                if (inserted) preparedInsertedCode = generated;
            }

            if (tpl.NeedsContainer)
            {
                var sourceSlot = _invStore.FindSlot(startMark);
                palletCode = Trim(sourceSlot?.PalletCode);
                cargoCode = Trim(sourceSlot?.CargoCode);
                dispatchContainer = !string.IsNullOrWhiteSpace(cargoCode) ? cargoCode : palletCode;
                if (string.IsNullOrWhiteSpace(dispatchContainer))
                {
                    if (preparedInsertedCode != null) _invStore.RollbackPreparedTaskStartContainer(startMark, preparedInsertedCode);
                    ClearLocks(taskId, startStorageTaskLockMark, destination.Mark, destinationIsStorage);
                    Fail(roundId, tpl.Label, stepNo, $"起点站点没有可移动库存：{startMark}");
                    return null;
                }
            }

            try { _completion.RegisterReservation(taskId, startStorageTaskLockMark, destination.Mark); }
            catch
            {
                if (preparedInsertedCode != null) _invStore.RollbackPreparedTaskStartContainer(startMark, preparedInsertedCode);
                ClearLocks(taskId, startStorageTaskLockMark, destination.Mark, destinationIsStorage);
                throw;
            }
        }

        var startWcs = WcsOf(startMark, stations) ?? startMark ?? "";
        var destinationWcs = destination!.ToWcsCode();
        var task = new WcsTaskItem
        {
            TaskId = taskId!, TaskType = tpl.Value, ContainerCode = dispatchContainer,
            PalletCode = palletCode, CargoCode = cargoCode,
            StationCode = [startWcs, destinationWcs], AreaCode = [],
        };
        var group = new WcsTaskGroup
        {
            GroupId = "G_" + Guid.NewGuid().ToString("N")[..10],
            MsgTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), PriorityCode = 5,
            Warehouse = settings.SceneName, Tasks = [task],
        };
        var taskUnits = new[] { task.PalletCode, task.CargoCode }
            .Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!running && halted)
        {
            _completion.CancelReservation(taskId!);
            if (preparedInsertedCode != null) _invStore.RollbackPreparedTaskStartContainer(startMark!, preparedInsertedCode);
            _invStore.RecyclePicked(taskUnits.Where(code => !string.Equals(code, preparedInsertedCode, StringComparison.OrdinalIgnoreCase)));
            return null;
        }

        (bool ok, int code, string json) result;
        try { result = await _modules.SendTaskWithModulesAsync(group, roundId); }
        catch { _completion.CancelReservation(taskId!); throw; }

        if (!result.ok)
        {
            _completion.CancelReservation(taskId!);
            if (result.code == -1) _invStore.MarkFail(taskUnits, taskId!);
            else
            {
                if (preparedInsertedCode != null) _invStore.RollbackPreparedTaskStartContainer(startMark!, preparedInsertedCode);
                _invStore.RecyclePicked(taskUnits.Where(code => !string.Equals(code, preparedInsertedCode, StringComparison.OrdinalIgnoreCase)));
            }
            Fail(roundId, tpl.Label, stepNo, $"任务下发失败：HTTP {result.code} {result.json[..Math.Min(result.json.Length, 200)]}");
            return null;
        }

        taskIds.Add(taskId!);
        taskDetails[taskId!] = $"{tpl.Label} [{dispatchContainer}] {startWcs}->{destinationWcs}";
        _invStore.MarkBusy(taskUnits);
        _completion.Activate(taskId!);
        ctx.LastTaskId = taskId;

        try
        {
            if ((tpl.End?.AfterModules?.Count ?? 0) > 0 || step.WaitForFinish)
            {
                if (!await _stage.WaitWcsCompletedAsync(taskId!))
                {
                    Fail(roundId, tpl.Label, stepNo, BuildCompletionFailureMessage(taskId!, tpl.Label));
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(roundId, tpl.Label, stepNo, $"等待 WCS 完成失败：{ex.Message}");
            return null;
        }
        return taskId;
    }

    private string? ResolveStartMark(string source, ExecCtx ctx, TaskTemplateDto tpl, List<MapStationLite> stations, string roundId, string stepNo)
    {
        if (source == AutoStartSources.SelectedStation)
        {
            if (!string.IsNullOrWhiteSpace(ctx.SelectedStartMark)) return ctx.SelectedStartMark;
            Fail(roundId, tpl.Label, stepNo, "已选起点模式没有选择站点");
            return null;
        }
        if (source == AutoStartSources.PreviousTaskEnd)
        {
            if (string.IsNullOrWhiteSpace(ctx.LastTaskId))
            {
                Fail(roundId, tpl.Label, stepNo, "上一任务终点模式没有上一任务");
                return null;
            }
            var completed = _stage.GetAll().LastOrDefault(record =>
                string.Equals(record.TaskId, ctx.LastTaskId, StringComparison.OrdinalIgnoreCase)
                && record.Stage == "WCS_COMPLETED" && record.IsSuccess);
            if (completed == null)
            {
                Fail(roundId, tpl.Label, stepNo, "上一任务尚未完成 WCS 收尾");
                return null;
            }
            return ToMapMark(completed.EndStationCode);
        }

        Fail(roundId, tpl.Label, stepNo, "起点来源必须选择“所选库存位置”或“上一步任务终点”");
        return null;
    }

    private static string ResolveStartSource(AutoStepDto step, ExecCtx ctx)
    {
        if (step.StartSource is AutoStartSources.SelectedStation or AutoStartSources.PreviousTaskEnd)
            return step.StartSource;
        // Existing saved templates remain usable until they are opened and saved in the new editor.
        if (step.UsePickedStart)
            return string.IsNullOrWhiteSpace(ctx.LastTaskId) ? AutoStartSources.SelectedStation : AutoStartSources.PreviousTaskEnd;
        return string.IsNullOrWhiteSpace(ctx.SelectedStartMark)
            ? (string.IsNullOrWhiteSpace(ctx.LastTaskId) ? AutoStartSources.SelectedStation : AutoStartSources.PreviousTaskEnd)
            : AutoStartSources.SelectedStation;
    }

    private static string NormalizeContainerMode(string? mode, bool needsContainer)
    {
        if (!needsContainer) return TaskContainerModes.ExistingInventory;
        return mode is TaskContainerModes.GenerateCargo or TaskContainerModes.GeneratePallet
            ? mode : TaskContainerModes.ExistingInventory;
    }

    private void ClearLocks(string taskId, string? startMark, string destinationMark, bool destinationIsStorage)
    {
        if (destinationIsStorage) _invStore.ClearTaskLock(taskId, destinationMark);
        if (startMark != null) _invStore.ClearTaskLock(taskId, startMark);
    }

    private void Fail(string roundId, string tplLabel, string stepNo, string message)
    {
        _log.Add(roundId, $"\u6b65\u9aa4 {stepNo} \u6a21\u677f[{tplLabel}] {message}", "#f87171");
        _log.AddOrUpdate("[\u6a21\u677f\u6b65\u9aa4\u5931\u8d25]", $"\u6a21\u677f\u300c{tplLabel}\u300d\uff1a{message}", "#f87171");
    }

    private string BuildCompletionFailureMessage(string taskId, string templateLabel)
    {
        var events = _stage.GetAll()
            .Where(record => string.Equals(record.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var reason = events.LastOrDefault(record =>
            record.Stage.StartsWith("WCS_FINALIZATION_REASON:", StringComparison.OrdinalIgnoreCase))?.Stage;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var detail = reason["WCS_FINALIZATION_REASON:".Length..].Trim();
            return $"WCS收尾失败：{TranslateCompletionReason(detail)}，任务号：{taskId}";
        }

        if (events.Any(record => record.Stage == "SORTING_SLOT_ASSIGN_FAILED"))
            return $"分拣失败：没有可用的分拣台，请先关联分拣台，任务号：{taskId}";

        var inventoryFailure = events.LastOrDefault(record =>
            record.Stage.StartsWith("INVENTORY_RELEASE_FAILED:", StringComparison.OrdinalIgnoreCase))?.Stage;
        if (!string.IsNullOrWhiteSpace(inventoryFailure))
        {
            var detail = inventoryFailure["INVENTORY_RELEASE_FAILED:".Length..].Trim();
            return $"库存收尾失败：{TranslateCompletionReason(detail)}，任务号：{taskId}";
        }

        var effectFailure = events.LastOrDefault(record =>
            record.Stage.StartsWith("EFFECT_CONFIGURATION:", StringComparison.OrdinalIgnoreCase)
            || record.Stage.StartsWith("PRE_EFFECT:", StringComparison.OrdinalIgnoreCase)
            || record.Stage.StartsWith("POST_EFFECT:", StringComparison.OrdinalIgnoreCase))?.Stage;
        if (!string.IsNullOrWhiteSpace(effectFailure))
            return $"{templateLabel}执行后的库存效果处理失败，任务号：{taskId}";

        return $"WCS收尾失败：WCS未能确认任务成功完成，任务号：{taskId}";
    }

    private static string TranslateCompletionReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "未提供具体原因";
        return reason switch
        {
            "终点库存收尾失败" => "终点库存处理失败",
            "终点后模块或库存效果失败" => "终点后的模块或库存效果处理失败",
            "NO_TRANSIT" => "找不到运输中的库存记录",
            _ => reason
        };
    }

    private static MapStationLite? ChooseDestination(TaskTemplateDto tpl, List<MapStationLite> stations, HashSet<string> occupied, string? excludeMark)
    {
        bool Free(MapStationLite station) => (station.StationType & MapStationTypeBits.PeopleStation) != 0
            || (!occupied.Contains(station.Mark) && (excludeMark == null || !string.Equals(station.Mark, excludeMark, StringComparison.OrdinalIgnoreCase)));
        MapStationLite? RandomPick(Func<MapStationLite, bool> predicate)
        {
            var pool = stations.Where(predicate).ToList();
            return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
        }
        var bits = tpl.End?.StationTypeBits ?? 0;
        if (bits != 0) return RandomPick(station => (station.StationType & bits) != 0 && Free(station));
        var value = $"{tpl.Value} {tpl.Category} {tpl.Label}".ToLowerInvariant();
        if (value.Contains("sort") || value.Contains("\u5206\u62e3"))
            return RandomPick(s => (s.StationType & MapStationTypeBits.PickingStation) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && Free(s));
        if (value.Contains("inbound") || value.Contains("\u5165\u5e93"))
            return RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && Free(s))
                ?? RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && Free(s));
        return RandomPick(s => (s.StationType & MapStationTypeBits.TransferPoint) != 0 && Free(s))
            ?? RandomPick(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0 && Free(s))
            ?? RandomPick(s => (s.StationType & MapStationTypeBits.PickingStation) != 0 && Free(s));
    }

    private static string GenerateContainerCode(string prefix, bool cargo)
    {
        var fallback = cargo ? "SimCargo_" : "SimContainer_";
        var value = string.IsNullOrWhiteSpace(prefix) ? fallback : prefix.Trim();
        if (cargo && !value.Contains("Cargo", StringComparison.OrdinalIgnoreCase)) value = fallback + value;
        if (!cargo && !value.Contains("Container", StringComparison.OrdinalIgnoreCase)) value = fallback + value;
        return value + DateTime.Now.ToString("HHmmssfff") + Random.Shared.Next(10, 99);
    }

    private static string Trim(string? value) => value?.Trim() ?? "";
    private static string? WcsOf(string? mark, List<MapStationLite> stations) => string.IsNullOrEmpty(mark) ? null : stations.FirstOrDefault(s => string.Equals(s.Mark, mark, StringComparison.OrdinalIgnoreCase))?.ToWcsCode() ?? mark;
    private static string ToMapMark(string stationCode)
    {
        var value = stationCode?.Trim() ?? "";
        var separator = value.LastIndexOf('_');
        return separator > 0 && int.TryParse(value[(separator + 1)..], out _) ? value[..separator] : value;
    }

    public class ExecCtx
    {
        public string? SelectedStartMark;
        public string? LastTaskId;
    }
}
