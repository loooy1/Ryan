using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Application.Scheduling;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

public interface IRcsTaskStore
{
    Task<List<RcsTaskRow>> FindAsync(IReadOnlyList<string> taskIds, CancellationToken token = default);
    Task<List<RcsTaskRow>> ListAsync(int limit, CancellationToken token = default);
    Task<RcsTaskPageRows> ListPageAsync(int page, int pageSize, string status, string search, CancellationToken token = default);
    Task<int> DeleteAllAsync(CancellationToken token = default);
    Task AddAsync(IReadOnlyList<RcsTaskRow> tasks, CancellationToken token = default);
    Task<IReadOnlyList<RcsTaskCandidate>> WaitingCandidatesAsync(string source, CancellationToken token = default);
    Task<List<RcsTaskRow>> WaitingTasksAsync(string source, CancellationToken token = default);
    Task<bool> TryStartAsync(RcsTaskRow task, CancellationToken token = default);
    Task UpdateAsync(RcsTaskRow task, CancellationToken token = default);
    Task RecoverAsync(CancellationToken token = default);
}

public sealed record RcsTaskPageRows(IReadOnlyList<RcsTaskRow> Items, int TotalCount, int GroupCount,
    int AllTaskCount, int WaitingCount, int ExecutingCount, int Page, int PageSize, int TotalPages);
