using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Realtime;

namespace WCSBackend.Modules.Wcs.Console.Services;

public sealed class ModuleExecutionLogStore
{
    private const int MaxRecords = 2000;
    private readonly object _writeLock = new();
    private readonly IUnitOfWorkFactory _uow;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public ModuleExecutionLogStore(IUnitOfWorkFactory uowFactory, IHubContext<TaskStageRealtimeHub> hub)
    {
        _uow = uowFactory;
        _hub = hub;
    }

    public ModuleExecLogEntry Record(ModuleExecLogEntry entry)
    {
        ModuleExecLogEntry result;
        lock (_writeLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<ModuleExecLogRow>();
            var row = new ModuleExecLogRow
            {
                TaskId = entry.TaskId ?? "",
                ModuleId = entry.ModuleId ?? "",
                ModuleName = entry.Module ?? "",
                ExecutionPhase = entry.ExecutionPhase ?? "",
                Status = entry.Status ?? "fail",
                HttpStatus = entry.HttpCode,
                DetailJson = entry.DetailJson ?? "{}",
                StartedAt = entry.StartedAt ?? DateTime.Now.ToString("O"),
                FinishedAt = entry.FinishedAt
            };
            repo.AddAsync(row).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
            Trim(repo, uow);
            result = ToEntry(row);
        }
        _ = _hub.Clients.All.SendAsync("ModuleExecLogAdded", result);
        return result;
    }

    public List<ModuleExecLogEntry> GetRecent(int take = 200)
    {
        take = Math.Clamp(take, 1, MaxRecords);
        using var uow = _uow.Create();
        return uow.Repository<ModuleExecLogRow>().Query()
            .OrderByDescending(row => row.Id).Take(take).ToList()
            .Select(ToEntry).ToList();
    }

    private static void Trim(IRepository<ModuleExecLogRow> repo, IUnitOfWork uow)
    {
        var staleIds = repo.Query().OrderByDescending(row => row.Id)
            .Skip(MaxRecords).Select(row => row.Id).ToList();
        if (staleIds.Count == 0) return;
        repo.DeleteWhereAsync(row => staleIds.Contains(row.Id)).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private static ModuleExecLogEntry ToEntry(ModuleExecLogRow row) => new()
    {
        Id = row.Id,
        Time = FormatTime(row.FinishedAt ?? row.StartedAt),
        TaskId = row.TaskId,
        ModuleId = row.ModuleId,
        Module = row.ModuleName,
        ExecutionPhase = row.ExecutionPhase,
        Point = ModuleExecLogEntry.ToPhaseLabel(row.ExecutionPhase),
        Status = row.Status,
        Ok = string.Equals(row.Status, "success", StringComparison.OrdinalIgnoreCase),
        HttpCode = row.HttpStatus,
        DetailJson = row.DetailJson,
        Detail = ModuleExecLogEntry.BuildSummary(row.DetailJson),
        StartedAt = row.StartedAt,
        FinishedAt = row.FinishedAt ?? ""
    };

    private static string FormatTime(string value)
        => DateTime.TryParse(value, out var time) ? time.ToString("HH:mm:ss") : value;
}
