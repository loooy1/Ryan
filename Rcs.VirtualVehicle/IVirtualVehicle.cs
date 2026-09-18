using Rcs.Contracts.Map;
using Rcs.Contracts.Vehicles;

namespace Rcs.VirtualVehicle;

public interface IVirtualVehicle
{
    VehicleStateDto State { get; }
    event Action<VehicleStateDto>? StateChanged;
    Task RunAsync(IReadOnlyList<GridPoint> route, CancellationToken cancellationToken = default);
    void Pause();
    void Reset(GridPoint position);
}
