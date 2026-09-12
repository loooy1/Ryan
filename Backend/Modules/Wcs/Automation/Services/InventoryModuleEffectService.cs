using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>Applies a successful module's configured cargo-only inventory action.</summary>
public sealed class InventoryModuleEffectService
{
    private readonly WcsInventoryStore _inventory;
    private readonly ITaskStageService _stages;

    public InventoryModuleEffectService(WcsInventoryStore inventory, ITaskStageService stages)
    {
        _inventory = inventory;
        _stages = stages;
    }

    public bool HandleSucceeded(string taskId, string? effect, string moduleStage)
    {
        var normalized = effect?.Trim().ToLowerInvariant() ?? ModuleInventoryEffects.None;
        if (string.IsNullOrEmpty(normalized)) return true;

        var atEnd = string.Equals(moduleStage, "AFTER_END_MODULE", StringComparison.OrdinalIgnoreCase);
        var success = normalized switch
        {
            ModuleInventoryEffects.CargoArrival => _inventory.WriteTaskCargoAt(taskId, atEnd),
            ModuleInventoryEffects.CargoRemoval => _inventory.RemoveTaskCargoAt(taskId, atEnd),
            _ => false,
        };
        _stages.TryRecordSystemEvent(taskId, $"INVENTORY_EFFECT:{normalized}", success, success ? 200 : 0);
        return success;
    }
}
