using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Application.Vehicles;

public interface IRcsVehicleStore
{
    Task<IReadOnlyList<RcsVehicleRow>> ListAsync(CancellationToken token = default);
    Task AddAsync(RcsVehicleRow row, CancellationToken token = default);
    Task UpdateAsync(RcsVehicleRow row, CancellationToken token = default);
    Task DeleteAsync(string vehicleId, CancellationToken token = default);
}
