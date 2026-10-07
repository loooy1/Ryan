using Backend.Shared.Infrastructure;
using Contracts.Rcs.Tasks;
using Microsoft.EntityFrameworkCore;
using RCSBackend.Modules.Rcs.Application.Tasks;
using RCSBackend.Modules.Rcs.Application.Scheduling;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Stores;

public sealed class RcsTaskStore(IDbContextFactory<GrcsDbContext> factory) : IRcsTaskStore
{
    public async Task<List<RcsTaskRow>> FindAsync(IReadOnlyList<string> taskIds, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Set<RcsTaskRow>().AsNoTracking().Where(x => taskIds.Contains(x.TaskId)).ToListAsync(token);
    }

    public async Task<List<RcsTaskRow>> ListAsync(int limit, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Set<RcsTaskRow>().AsNoTracking().OrderByDescending(x => x.Id).Take(limit).ToListAsync(token);
    }

    public async Task<RcsTaskPageRows> ListPageAsync(int page, int pageSize, string status, string search,
        CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var all = db.Set<RcsTaskRow>().AsNoTracking();
        var filtered = all.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var query = search.Trim();
            filtered = filtered.Where(x => x.TaskId.Contains(query) || x.GroupId.Contains(query)
                || x.VehicleId.Contains(query) || x.RequestedVehicleId.Contains(query)
                || x.ContainerCode.Contains(query) || x.Warehouse.Contains(query) || x.TaskType.Contains(query)
                || x.StationCodesJson.Contains(query) || x.AreaCodesJson.Contains(query));
        }

        var totalCount = await filtered.CountAsync(token);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var items = await filtered.OrderByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var allTaskCount = await all.CountAsync(token);
        var groupCount = await all.Select(x => x.GroupId).Distinct().CountAsync(token);
        var waitingCount = await all.CountAsync(x => x.Status == RcsTaskStatus.Waiting, token);
        var executingCount = await all.CountAsync(x => x.Status == RcsTaskStatus.Running
            || x.Status == RcsTaskStatus.Paused || x.Status == RcsTaskStatus.Cancelling, token);
        return new(items, totalCount, groupCount, allTaskCount, waitingCount, executingCount, page, pageSize, totalPages);
    }

    public async Task AddAsync(IReadOnlyList<RcsTaskRow> tasks, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        db.Set<RcsTaskRow>().AddRange(tasks);
        // 同一报文的新任务一次提交，任何一条失败均不接受半个批次。
        await db.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<RcsTaskCandidate>> WaitingCandidatesAsync(string source, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Set<RcsTaskRow>().AsNoTracking().Where(x => x.Status == RcsTaskStatus.Waiting && x.Source == source)
            .Select(x => new RcsTaskCandidate(x.Id, x.TaskId, x.PriorityCode, x.RequestedVehicleId)).ToListAsync(token);
    }

    public async Task<List<RcsTaskRow>> WaitingTasksAsync(string source, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Set<RcsTaskRow>().Where(x => x.Status == RcsTaskStatus.Waiting && x.Source == source)
            .OrderBy(x => x.PriorityCode).ThenBy(x => x.Id).ToListAsync(token);
    }

    public async Task<bool> TryStartAsync(RcsTaskRow task, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var now = DateTime.UtcNow;
        return await db.Set<RcsTaskRow>().Where(x => x.Id == task.Id && x.Status == RcsTaskStatus.Waiting)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, RcsTaskStatus.Running)
                .SetProperty(x => x.VehicleId, task.VehicleId).SetProperty(x => x.StartedAt, task.StartedAt)
                .SetProperty(x => x.UpdatedAt, now), token) == 1;
    }

    public async Task UpdateAsync(RcsTaskRow task, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        task.UpdatedAt = DateTime.UtcNow;
        db.Update(task);
        await db.SaveChangesAsync(token);
    }

    public async Task<int> DeleteAllAsync(CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Set<RcsTaskRow>().ExecuteDeleteAsync(token);
    }

    public async Task RecoverAsync(CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var unfinished = await db.Set<RcsTaskRow>().Where(x => x.Status == RcsTaskStatus.Running
            || x.Status == RcsTaskStatus.Paused || x.Status == RcsTaskStatus.Cancelling).ToListAsync(token);
        foreach (var task in unfinished)
        {
            task.Status = RcsTaskStatus.Interrupted;
            task.Message = "RCS 重启，原执行任务已中断，请确认车辆位置后重新下发新任务。";
            task.FinishedAt = task.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(token);
    }
}
