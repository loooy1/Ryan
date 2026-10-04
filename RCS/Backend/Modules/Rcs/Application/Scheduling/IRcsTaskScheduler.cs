using Contracts.Rcs.Vehicle;

namespace RCSBackend.Modules.Rcs.Application.Scheduling;

public interface IRcsTaskScheduler
{
    RcsTaskAssignment? Select(IReadOnlyList<RcsTaskCandidate> waitingTasks,
        IReadOnlyList<VehicleStateDto> vehicles);
}
