using Contracts.Rcs.Tasks;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

/// <summary>任务只读查询，与接收、调度和执行生命周期分离。</summary>
public interface IRcsTaskQueryService
{
    Task<IReadOnlyList<RcsTaskDto>> ListAsync(int limit, CancellationToken token = default);
    Task<RcsTaskPageDto> ListPageAsync(int page, int pageSize, string status, string search,
        CancellationToken token = default);
    Task<RcsTaskDto?> GetAsync(string taskId, CancellationToken token = default);
}
