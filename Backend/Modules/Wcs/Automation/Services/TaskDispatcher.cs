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
            Fail(roundId, tplLabel: step.TemplateValue, stepNo, "task template is missing");
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
                    Fail(roundId, tpl.Label, stepNo, $"start station type does not match: {startMark}");
                    return null;
                }
            }

            destination = ChooseDestination(tpl, stations, occupied, startMark);
            if (destination == null)
            {
                Fail(roundId, tpl.Label, stepNo, "no destination station is available");
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
                    Fail(roundId, tpl.Label, stepNo, $"start storage is unavailable: {startMark}");
                    return null;
                }
                startStorageTaskLockMark = startMark;
            }

            destinationIsStorage = (destination.StationType & MapStationTypeBits.StorageLocation) != 0;
            if (destinationIsStorage && !_invStore.TryLockDestinationForTask(taskId, destination.Mark))
            {
                ClearLocks(taskId, startStorageTaskLockMark, destination.Mark, destinationIsStorage);
                Fail(roundId, tpl.Label, stepNo, $"destination storage is unavailable: {destination.Mark}");
                return null;
            }

            var mode = NormalizeContainerMode(tpl.ContainerMode, tpl.NeedsContainer);

            if (mode is TaskContainerModes.GenerateCargo or TaskContainerModes.GeneratePallet)
            {
                var generated = GenerateContainerCode(tpl.ContainerPrefix, mode == TaskContainerModes.GenerateCargo);
                if (!_invStore.TryPrepareTaskStartContainer(startMark, generated, out var inserted, out var prepareError))
                {
                    ClearLocks(taskId, startStorageTaskLockMark, destination.Mark, destinationIsStorage);
                    Fail(roundId, tpl.Label, stepNo, $"start container preparation failed: {prepareError}");
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
                    Fail(roundId, tpl.Label, stepNo, $"start station has no movable inventory: {startMark}");
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
            Fail(roundId, tpl.Label, stepNo, $"dispatch failed: HTTP {result.code} {result.json[..Math.Min(result.json.Length, 200)]}");
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
                    Fail(roundId, tpl.Label, stepNo, $"WCS completion failed: {taskId}");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(roundId, tpl.Label, stepNo, $"completion wait failed: {ex.Message}");
            return null;
        }
        return taskId;
    }

    private string? ResolveStartMark(string source, ExecCtx ctx, TaskTemplateDto tpl, List<MapStationLite> stations, string roundId, string stepNo)
    {
        if (source == AutoStartSources.SelectedStation)
        {
            if (!string.IsNullOrWhiteSpace(ctx.SelectedStartMark)) return ctx.SelectedStartMark;
            Fail(roundId, tpl.Label, stepNo, "selected-start source has no selected station");
            return null;
        }
        if (source == AutoStartSources.PreviousTaskEnd)
        {
            if (string.IsNullOrWhiteSpace(ctx.LastTaskId))
            {
                Fail(roundId, tpl.Label, stepNo, "previous-task-end source has no previous task");
                return null;
            }
            var completed = _stage.GetAll().LastOrDefault(record =>
                string.Equals(record.TaskId, ctx.LastTaskId, StringComparison.OrdinalIgnoreCase)
                && record.Stage == "WCS_COMPLETED" && record.IsSuccess);
            if (completed == null)
            {
                Fail(roundId, tpl.Label, stepNo, "previous task has not reached WCS_COMPLETED");
                return null;
            }
            return ToMapMark(completed.EndStationCode);
        }

        var bits = tpl.Start?.StationTypeBits ?? 0;
        if (bits == 0)
        {
            Fail(roundId, tpl.Label, stepNo, "automatic start selection requires a start type");
            return null;
        }
        var availableStorage = _invStore.GetStartAvailableStorageMarks();
        var pool = stations.Where(station => (station.StationType & bits) != 0
            && ((station.StationType & MapStationTypeBits.StorageLocation) == 0 || availableStorage.Contains(station.Mark))).ToList();
        if (pool.Count == 0)
        {
            Fail(roundId, tpl.Label, stepNo, "no matching start station is available");
            return null;
        }
        return pool[Random.Shared.Next(pool.Count)].Mark;
    }

    private static string ResolveStartSource(AutoStepDto step, ExecCtx ctx)
    {
        if (step.StartSource is AutoStartSources.SelectedStation or AutoStartSources.PreviousTaskEnd or AutoStartSources.AutoSelect)
            return step.StartSource;
        // Existing saved templates remain usable until they are opened and saved in the new editor.
        if (step.UsePickedStart)
            return string.IsNullOrWhiteSpace(ctx.LastTaskId) ? AutoStartSources.SelectedStation : AutoStartSources.PreviousTaskEnd;
        return AutoStartSources.AutoSelect;
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
