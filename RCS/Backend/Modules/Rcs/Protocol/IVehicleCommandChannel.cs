using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;

namespace RCSBackend.Modules.Rcs.Protocol;

/// <summary>执行服务使用的协议通道。以后更换 TCP/HTTP 通道，不改变任务和调度接口。</summary>
public interface IVehicleCommandChannel
{
    event Action<VehicleStateDto>? StateChanged;
    IReadOnlyList<VehicleStateDto> GetStates();
    Task<VehicleCommandAck> SendAsync(VehicleCommand command, CancellationToken token = default);
    Task<VehicleCommandResult> WaitForCompletionAsync(string vehicleId, string commandId, CancellationToken token = default);
}
