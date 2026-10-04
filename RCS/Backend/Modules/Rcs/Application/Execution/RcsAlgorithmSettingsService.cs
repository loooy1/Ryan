using Backend.Shared.Infrastructure;
using Contracts.Rcs.Algorithm;
using Microsoft.EntityFrameworkCore;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>Persists algorithm settings and exposes the current immutable snapshot to task execution.</summary>
public sealed class RcsAlgorithmSettingsService(IDbContextFactory<GrcsDbContext> factory)
{
    private readonly object _gate = new();
    private AlgorithmSettingsDto _current = new();

    public AlgorithmSettingsDto Current { get { lock (_gate) return _current; } }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.Set<RcsAlgorithmSettingsRow>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, token);
        if (row is null)
        {
            row = new RcsAlgorithmSettingsRow { Id = 1, UpdatedAt = DateTime.UtcNow };
            db.Add(row);
            await db.SaveChangesAsync(token);
        }
        SetCurrent(ToDto(row));
    }

    public async Task<AlgorithmSettingsDto> SaveAsync(AlgorithmSettingsDto value, CancellationToken token = default)
    {
        Validate(value);
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.Set<RcsAlgorithmSettingsRow>().FirstOrDefaultAsync(x => x.Id == 1, token);
        if (row is null)
        {
            row = new RcsAlgorithmSettingsRow { Id = 1 };
            db.Add(row);
        }
        row.SegmentPointCount = value.SegmentPointCount;
        row.AdvanceAfterPoints = value.AdvanceAfterPoints;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        var saved = ToDto(row);
        SetCurrent(saved);
        return saved;
    }

    private void SetCurrent(AlgorithmSettingsDto value) { lock (_gate) _current = value; }
    private static AlgorithmSettingsDto ToDto(RcsAlgorithmSettingsRow row) => new()
        { SegmentPointCount = row.SegmentPointCount, AdvanceAfterPoints = row.AdvanceAfterPoints };

    private static void Validate(AlgorithmSettingsDto value)
    {
        if (value.SegmentPointCount is < 2 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(value), "每段下发点数必须在 2 到 1000 之间。");
        if (value.AdvanceAfterPoints < 1 || value.AdvanceAfterPoints >= value.SegmentPointCount)
            throw new ArgumentOutOfRangeException(nameof(value), "续发间隔必须在 1 到每段下发点数减 1 之间，以确保续发时保留当前位置重叠点。");
    }
}
