using Contracts.Rcs.Vehicle;

namespace RCSBackend.Modules.Rcs.Application.Execution;

public interface IRcsTaskExecutionService
{
    event Action<VehicleStateDto>? VehicleStateChanged;
    IReadOnlyList<VehicleStateDto> GetVehicles();
    RcsTaskExecutionPlan Prepare(RcsTaskExecutionRequest request);
    void ClearVehiclePlanningGoal(string vehicleId);
    Task ExecuteAsync(RcsTaskExecutionPlan plan, bool startPaused, CancellationToken token);
    Task PauseAsync(string vehicleId, CancellationToken token = default);
    Task ResumeAsync(string vehicleId, CancellationToken token = default);
    Task<RcsTaskExecutionPlan> ReplanAsync(string taskId, CancellationToken token = default);
    void ValidateReset(string vehicleId, string pointCode);
    Task ResetAsync(string vehicleId, string pointCode, CancellationToken token = default);
}
