using Contracts.Rcs.Protocol;
using Contracts.Rcs.Tasks;
using Contracts.Rcs.Vehicle;
using RCSBackend.Modules.Rcs.Protocol;
using RCSBackend.Modules.Rcs.Application.Inventory;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>Executes a vehicle fetch/put command and commits inventory only after vehicle confirmation.</summary>
public sealed class RcsVehicleActionExecutor(
    IVehicleCommandChannel channel,
    RcsInventoryTransferService inventory)
{
    public async Task SendAsync(VehicleCommand command, Action accepted, Action publish, CancellationToken token)
    {
        var acknowledgement = await channel.SendAsync(command, token);
        if (!acknowledgement.Accepted) throw new InvalidOperationException(acknowledgement.Message);
        accepted();
        publish();
    }

    public async Task<string> WaitForCompletionAndApplyInventoryAsync(RcsTaskExecutionPlan plan, RcsTaskStop stop,
        string commandId, Func<VehicleStateDto> getVehicle, Action publish, CancellationToken token)
    {
        var result = await channel.WaitForCompletionAsync(plan.VehicleId, commandId, token);
        if (result.Status != "COMPLETED") throw new InvalidOperationException(result.Message);
        var confirmed = getVehicle();
        if (confirmed.TaskId != plan.TaskId || confirmed.PointCode != stop.PointCode
            || !confirmed.CompletedStepIds.Contains(stop.StepId, StringComparer.Ordinal)
            || confirmed.LastAction != stop.Action)
            throw new InvalidOperationException($"车辆未确认在 {stop.PointCode} 完成 {stop.Action} 命令。");

        if (plan.SyncInventory) await inventory.ApplyCompletedActionAsync(plan.TaskId, stop.Action, token);
        publish();
        return commandId;
    }
}
