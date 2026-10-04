using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Application.Scheduling;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

public interface IRcsTaskStore
{
    Task<List<RcsTaskRow>> FindAsync(IReadOnlyList<string> taskIds, CancellationToken token = default);
    Task<List<RcsTaskRow>> ListAsync(int limit, CancellationToken token = default);
    Task AddAsync(IReadOnlyList<RcsTaskRow> tasks, CancellationToken token = default);
    Task<IReadOnlyList<RcsTaskCandidate>> WaitingCandidatesAsync(string source, CancellationToken token = default);
    Task<bool> TryStartAsync(RcsTaskRow task, CancellationToken token = default);
    Task UpdateAsync(RcsTaskRow task, CancellationToken token = default);
    Task RecoverAsync(CancellationToken token = default);
}
