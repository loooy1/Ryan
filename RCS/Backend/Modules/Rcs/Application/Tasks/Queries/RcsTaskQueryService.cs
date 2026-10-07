using Contracts.Rcs.Tasks;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

public sealed class RcsTaskQueryService(IRcsTaskStore store) : IRcsTaskQueryService
{
    public async Task<IReadOnlyList<RcsTaskDto>> ListAsync(int limit, CancellationToken token = default) =>
        (await store.ListAsync(Math.Clamp(limit, 1, 500), token)).Select(x => x.ToDto()).ToArray();

    public async Task<RcsTaskPageDto> ListPageAsync(int page, int pageSize, string status, string search,
        CancellationToken token = default)
    {
        pageSize = Math.Clamp(pageSize / 10 * 10, 10, 100);
        search = (search ?? "").Trim();
        if (search.Length > 128) search = search[..128];
        var rows = await store.ListPageAsync(Math.Max(page, 1), pageSize, status?.Trim() ?? "", search, token);
        return new RcsTaskPageDto
        {
            Items = rows.Items.Select(x => x.ToDto()).ToArray(), TotalCount = rows.TotalCount,
            AllTaskCount = rows.AllTaskCount, GroupCount = rows.GroupCount,
            WaitingCount = rows.WaitingCount, ExecutingCount = rows.ExecutingCount,
            Page = rows.Page, PageSize = rows.PageSize, TotalPages = rows.TotalPages
        };
    }

    public async Task<RcsTaskDto?> GetAsync(string taskId, CancellationToken token = default) =>
        (await store.FindAsync([taskId], token)).FirstOrDefault()?.ToDto();
}
