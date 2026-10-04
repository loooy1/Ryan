using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;

namespace Rcs.VirtualVehicle;

public interface IVirtualVehicle
{
    VehicleStateDto State { get; }
    event Action<VehicleStateDto>? StateChanged;
    VehicleCommandAck Receive(VehicleCommand command);
    Task<VehicleCommandResult> WaitForCompletionAsync(string commandId, CancellationToken token = default);
}
