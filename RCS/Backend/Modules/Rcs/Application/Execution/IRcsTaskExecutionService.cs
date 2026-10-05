using Contracts.Rcs.Vehicle;
using Contracts.Rcs.Tasks;

namespace RCSBackend.Modules.Rcs.Application.Execution;

public interface IRcsTaskExecutionService
{
    event Action<VehicleStateDto>? VehicleStateChanged;
    event Action? InventoryChanged;
    IReadOnlyList<VehicleStateDto> GetVehicles();
    RcsTaskExecutionPlan Prepare(RcsTaskExecutionRequest request);
    void ClearVehiclePlanningGoal(string vehicleId);
    Task ExecuteAsync(RcsTaskExecutionPlan plan, bool startPaused,
        Func<RcsTaskExecutionStageDto, Task>? stageChanged, CancellationToken token);
    Task PauseAsync(string vehicleId, CancellationToken token = default);
    Task ResumeAsync(string vehicleId, CancellationToken token = default);
    Task<RcsTaskExecutionPlan> ReplanAsync(string taskId, CancellationToken token = default);
    void ValidateReset(string vehicleId, string pointCode);
    Task ResetAsync(string vehicleId, string pointCode, CancellationToken token = default);
}
