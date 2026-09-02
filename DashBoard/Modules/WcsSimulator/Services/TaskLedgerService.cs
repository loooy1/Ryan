using GrcsBackend.Contracts.Dtos;
using GRCS.Dashboard.Modules.WcsSimulator.Services;

namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 任务台账遥控壳（Skill E：数据源为后端 task_records 合并表）。
/// GetAsync 改读 TaskStageHub 缓存（创建行 stage=CREATED，SignalR 实时推送，零 HTTP）；
/// AppendAsync 转 POST（后端写创建行 + 广播，手动任务/自动任务同一份）；
/// ClearAsync 转 DELETE（后端清空全表 + 广播 EventsReset）。换浏览器/清缓存数据仍在。
/// </summary>
public class TaskLedgerService
{
    private readonly WcsApiClient _api;
    private readonly TaskStageHub _hub;

    public TaskLedgerService(WcsApiClient api, TaskStageHub hub)
    {
        _api = api;
        _hub = hub;
    }

    /// <summary>读创建行（TaskStageHub 缓存投影，SignalR 实时，无 HTTP）。</summary>
    public async Task<List<TaskLedgerEntry>> GetAsync()
    {
        await _hub.EnsureStartedAsync();
        return _hub.CreatedTasks.ToList();
    }

    /// <summary>追加条目（后端写创建行并广播，前端缓存由 EventAdded 实时合并）。</summary>
    public async Task AppendAsync(List<TaskLedgerEntry> entries)
    {
        if (entries.Count == 0) return;
        await _api.PostAsync("/api/wcs/ledger", entries);
    }

    public async Task ClearAsync() => await _api.DeleteAsync("/api/wcs/ledger");
}