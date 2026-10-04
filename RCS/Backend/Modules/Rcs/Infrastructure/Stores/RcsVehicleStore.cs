using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RCSBackend.Modules.Rcs.Application.Vehicles;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Stores;

public sealed class RcsVehicleStore(IDbContextFactory<GrcsDbContext> factory) : IRcsVehicleStore
{
    public async Task<IReadOnlyList<RcsVehicleRow>> ListAsync(CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Set<RcsVehicleRow>().AsNoTracking().OrderBy(x => x.VehicleId).ToListAsync(token);
    }
    public async Task AddAsync(RcsVehicleRow row, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        db.Add(row); await db.SaveChangesAsync(token);
    }
    public async Task UpdateAsync(RcsVehicleRow row, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        db.Update(row); await db.SaveChangesAsync(token);
    }
    public async Task DeleteAsync(string vehicleId, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Set<RcsVehicleRow>().Where(x => x.VehicleId == vehicleId).ExecuteDeleteAsync(token);
    }
}
