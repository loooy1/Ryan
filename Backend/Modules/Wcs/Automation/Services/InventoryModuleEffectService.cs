using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 模块效果管线：HTTP 前预处理、HTTP 成功后的库存/任务效果，以及明确失败时的回滚。
/// 预处理的持久化资源只写入 WCS 数据库；ModuleCtx 仅在当前 HTTP 调用内传递变量。
/// </summary>
public sealed class ModuleEffectService
{
    private readonly WcsInventoryStore _inventory;
    private readonly ITaskStageService _stages;

    public ModuleEffectService(WcsInventoryStore inventory, ITaskStageService stages)
    {
        _inventory = inventory;
        _stages = stages;
    }

    public bool HandleBefore(ModuleRunService.ModuleCtx ctx, string? effect)
    {
        var normalized = effect?.Trim().ToLowerInvariant() ?? ModulePreExecutionEffects.None;
        if (string.IsNullOrEmpty(normalized)) return true;

        return normalized switch
        {
            ModulePreExecutionEffects.PrepareReturnTask => PrepareReturnTask(ctx),
            _ => false,
        };
    }

    public bool Validate(FeatureModuleDto module, out string message)
    {
        var before = module.PreExecutionEffect?.Trim().ToLowerInvariant() ?? "";
        var after = module.InventoryEffect?.Trim().ToLowerInvariant() ?? "";
        if (before == ModulePreExecutionEffects.PrepareReturnTask)
        {
            if (after != ModuleInventoryEffects.CreateReturnTask)
            {
                message = "回库任务预处理必须搭配“创建回库任务”成功后效果。";
                return false;
            }
            if (!HasContextParameter(module, "TaskId", "returnTaskId")
                || !HasContextParameter(module, "StationCode", "returnDestination"))
            {
                message = "回库任务模块的 TaskId、StationCode 必须分别使用上下文变量 returnTaskId、returnDestination。";
                return false;
            }
            if (!HasParameter(module, "ContainerCode") || !HasParameter(module, "RemoveContainer") || !HasParameter(module, "AreaCode"))
            {
                message = "回库任务模块必须包含 ContainerCode、RemoveContainer、AreaCode 参数。";
                return false;
            }
        }
        else if (after == ModuleInventoryEffects.CreateReturnTask)
        {
            message = "“创建回库任务”成功后效果必须搭配“回库任务预处理”。";
            return false;
        }

        message = "";
        return true;
    }

    public void HandleFailed(ModuleRunService.ModuleCtx ctx, string? effect)
    {
        var normalized = effect?.Trim().ToLowerInvariant() ?? ModulePreExecutionEffects.None;
        if (normalized != ModulePreExecutionEffects.PrepareReturnTask) return;

        var returnTaskId = GetReturnTaskId(ctx);
        foreach (var slot in _inventory.Slots().Where(slot =>
                     string.Equals(slot.TaskLockId, returnTaskId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(slot.SelectionStatus, WcsInventoryStore.SelectionTaskEndLocked,
                         StringComparison.OrdinalIgnoreCase)))
            _inventory.ClearTaskLock(returnTaskId, slot.Mark);
    }

    public bool HandleSucceeded(ModuleRunService.ModuleCtx ctx, string? effect, string moduleStage)
    {
        var normalized = effect?.Trim().ToLowerInvariant() ?? ModuleInventoryEffects.None;
        if (string.IsNullOrEmpty(normalized)) return true;

        var atEnd = string.Equals(moduleStage, "AFTER_END_MODULE", StringComparison.OrdinalIgnoreCase);
        var success = normalized switch
        {
            ModuleInventoryEffects.CargoArrival => _inventory.WriteTaskCargoAt(ctx.TaskId, atEnd),
            ModuleInventoryEffects.CargoRemoval => _inventory.RemoveTaskCargoAt(ctx.TaskId, atEnd),
            ModuleInventoryEffects.CreateReturnTask => CreateReturnTask(ctx),
            _ => false,
        };
        _stages.TryRecordSystemEvent(ctx.TaskId, $"INVENTORY_EFFECT:{normalized}", success, success ? 200 : 0);
        return success;
    }

    private bool PrepareReturnTask(ModuleRunService.ModuleCtx ctx)
    {
        var returnTaskId = GetReturnTaskId(ctx);
        if (string.IsNullOrWhiteSpace(ctx.TaskId) || string.IsNullOrWhiteSpace(ctx.End)) return false;

        var registered = _stages.GetAll().Any(record => record.IsCreated
            && string.Equals(record.TaskId, returnTaskId, StringComparison.OrdinalIgnoreCase));
        var locked = _inventory.Slots().Where(slot =>
                string.Equals(slot.TaskLockId, returnTaskId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.SelectionStatus, WcsInventoryStore.SelectionTaskEndLocked,
                    StringComparison.OrdinalIgnoreCase))
            .Select(slot => slot.Mark).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (locked.Count > 1) return false;
        var destination = locked.SingleOrDefault() ?? "";
        if (registered && string.IsNullOrWhiteSpace(destination))
        {
            destination = _stages.GetAll().LastOrDefault(record => record.IsCreated
                && string.Equals(record.TaskId, returnTaskId, StringComparison.OrdinalIgnoreCase))?.EndStationCode ?? "";
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            var storageMarks = _inventory.StorageMarks();
            var preferred = ToMapMark(ctx.Start);
            var candidates = _inventory.Slots()
                .Where(slot => storageMarks.Contains(slot.Mark))
                .Select(slot => slot.Mark)
                .OrderBy(mark => mark, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (storageMarks.Contains(preferred))
            {
                candidates.RemoveAll(mark => string.Equals(mark, preferred, StringComparison.OrdinalIgnoreCase));
                candidates.Insert(0, preferred);
            }
            destination = candidates.FirstOrDefault(mark => _inventory.TryLockDestinationForTask(returnTaskId, mark)) ?? "";
            if (string.IsNullOrWhiteSpace(destination)) return false;
        }

        ctx.SetContext("returnTaskId", returnTaskId);
        ctx.SetContext("returnDestination", destination);
        ctx.SetContext("returnTaskRegistered", registered ? "true" : "false");
        return true;
    }

    private bool CreateReturnTask(ModuleRunService.ModuleCtx ctx)
    {
        var returnTaskId = GetReturnTaskId(ctx);
        var destination = ctx.GetContext("returnDestination");
        if (string.IsNullOrWhiteSpace(returnTaskId) || string.IsNullOrWhiteSpace(destination)) return false;
        if (_stages.GetAll().Any(record => record.IsCreated
            && string.Equals(record.TaskId, returnTaskId, StringComparison.OrdinalIgnoreCase))) return true;

        _stages.RecordCreated([
            new TaskLedgerEntry
            {
                TaskId = returnTaskId,
                TaskType = "RCS_RETURN",
                ContainerCode = ctx.Pallet,
                CargoCode = ctx.Cargo,
                StartStationCode = ToMapMark(ctx.End),
                EndStationCode = ToMapMark(destination),
                Warehouse = ctx.Warehouse,
                Time = DateTime.Now.ToString("O"),
                Ok = true,
                StatusCode = 200,
            }
        ]);
        _stages.TryRecordSystemEvent(returnTaskId, "RETURN_TASK_REGISTERED", true, 200);
        ctx.SetContext("returnTaskRegistered", "true");
        return true;
    }

    private static string GetReturnTaskId(ModuleRunService.ModuleCtx ctx)
        => ctx.GetContext("returnTaskId") is { Length: > 0 } prepared
            ? prepared
            : ctx.TaskId + "_R";

    private static bool HasParameter(FeatureModuleDto module, string name)
        => module.Params.Any(parameter => string.Equals(parameter.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private static bool HasContextParameter(FeatureModuleDto module, string name, string variable)
        => module.Params.Any(parameter => string.Equals(parameter.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)
            && parameter.Source == WorkValueSourceDto.ModuleContext
            && string.Equals(parameter.FixedValue?.Trim(), variable, StringComparison.OrdinalIgnoreCase));

    private static string ToMapMark(string stationCode)
    {
        var value = stationCode.Trim();
        var separator = value.LastIndexOf('_');
        return separator > 0 && int.TryParse(value[(separator + 1)..], out _)
            ? value[..separator]
            : value;
    }
}
