using Contracts.Rcs.Vehicle;
using Contracts.Rcs.Tasks;

namespace RCSBackend.Modules.Rcs.Application.Scheduling;

/// <summary>优先级/FIFO；指定车辆不可用时，其他车辆仍可领取适合自己的任务。</summary>
public sealed class RcsTaskScheduler : IRcsTaskScheduler
{
    public RcsTaskAssignment? Select(IReadOnlyList<RcsTaskCandidate> waitingTasks,
        IReadOnlyList<VehicleStateDto> vehicles)
    {
        foreach (var task in waitingTasks.OrderBy(x => x.PriorityCode).ThenBy(x => x.QueueId))
        {
            var requiredMode = task.Source == RcsTaskSource.Manual ? RcsOperatingMode.Manual : RcsOperatingMode.Automatic;
            var available = vehicles.Where(x => x.IsEnabled && x.IsOnline && x.OperatingMode == requiredMode
                    && (x.Status is "Idle" or "Arrived"))
                .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var vehicle = available.FirstOrDefault(x => task.RequestedVehicleId == ""
                || string.Equals(x.Id, task.RequestedVehicleId, StringComparison.OrdinalIgnoreCase));
            if (vehicle is not null) return new(task.QueueId, task.TaskId, vehicle.Id);
        }
        return null;
    }
}
