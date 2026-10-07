using Contracts.Rcs.Tasks;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

public interface IRcsTaskService
{
    event Action<RcsTaskDto>? TaskChanged;
    event Action<int>? TasksCleared;
    Task<RcsApiResponse> ReceiveAsync(RcsTaskReceiveRequest request, string originalRequestJson, CancellationToken token = default);
    Task<RcsApiResponse> ReceiveManualAsync(RcsTaskReceiveRequest request, CancellationToken token = default);
    Task<int> ClearAllAsync(CancellationToken token = default);
    Task<bool> PauseAsync(string? taskId = null, CancellationToken token = default);
    Task<bool> ResumeAsync(string? taskId = null, CancellationToken token = default);
    Task<bool> CancelAsync(string taskId, CancellationToken token = default);
    Task<RcsTaskDto> ReplanAsync(string taskId, CancellationToken token = default);
    Task ResetVehicleAsync(string pointCode, CancellationToken token = default, string vehicleId = "V-01", Func<Task>? savePosition = null);
    Task SetVehiclePositionAsync(string pointCode, CancellationToken token = default, string vehicleId = "V-01", Func<Task>? savePosition = null);
    Task<bool> PauseVehicleAsync(string vehicleId, CancellationToken token = default);
    Task<bool> ResumeVehicleAsync(string vehicleId, CancellationToken token = default);
    Task<bool> StopVehicleAsync(string vehicleId, CancellationToken token = default);
    bool IsVehicleBusy(string vehicleId);
    void NotifyWork();
}
