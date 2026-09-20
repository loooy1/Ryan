using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using Microsoft.AspNetCore.SignalR;

namespace WCSBackend.Modules.Wcs.Realtime;

/// <summary>
/// WCS 实时数据推送 Hub（/hubs/wcs-realtime）。
/// 前端订阅本 Hub 接收：
/// ① 合并表（task_records）实时变更：新记录（创建行或阶段行）到达（TaskStageService.Record / RecordCreated）
///    即时广播 EventAdded，删除广播 TaskRemoved，清空广播 EventsReset(空表)；
/// ② 纯移动任务循环状态（MoveLoopRunner 每轮广播 MoveTaskStats，连接建立时回放当前快照）；
/// ③ 归巢模式状态（NestRunner 广播 NestStats，连接建立时回放快照）；
/// ④ 请求信号记录（MockApprovalService 每次变更广播全量 MockRequestEvents，连接建立时回放快照）；
/// ⑤ 模块执行记录包含在 task_records 的模块阶段行中，随 EventsReset / EventAdded 推送。
/// </summary>
public class TaskStageRealtimeHub : Hub
{
    private readonly ITaskStageService _stages;
    private readonly MoveLoopRunner _moveLoop;
    private readonly NestRunner _nest;
    private readonly MockApprovalService _mockApproval;
    private readonly ModuleExecutionLogStore _moduleLogs;
    private readonly AutomationRealtimePublisher _automation;

    public TaskStageRealtimeHub(ITaskStageService stages, MoveLoopRunner moveLoop, NestRunner nest,
        MockApprovalService mockApproval, ModuleExecutionLogStore moduleLogs, AutomationRealtimePublisher automation)
    {
        _stages = stages;
        _moveLoop = moveLoop;
        _nest = nest;
        _mockApproval = mockApproval;
        _moduleLogs = moduleLogs;
        _automation = automation;
    }

    public override async Task OnConnectedAsync()
    {
        // 回放全表快照（创建行 + 阶段行，等价于旧轮询的全量首拉），让新连接立即有数据
        await Clients.Caller.SendAsync("EventsReset", _stages.GetAll());
        // 回放纯移动任务循环状态（重连/新标签页立即有统计）
        await Clients.Caller.SendAsync("MoveTaskStats", _moveLoop.Snapshot());
        // 回放归巢模式状态
        await Clients.Caller.SendAsync("NestStats", _nest.Snapshot());
        // 回放请求信号记录全量（低频变更，全量快照最简可靠）
        await Clients.Caller.SendAsync("MockRequestEvents", _mockApproval.GetEvents());
        await Clients.Caller.SendAsync("ModuleExecLogsReset", new { maxId = 0L, entries = _moduleLogs.GetRecent() });
        await Clients.Caller.SendAsync("AutomationStatus", _automation.CurrentStatus);
        await Clients.Caller.SendAsync("AutomationLogs", _automation.CurrentLogs);
        await Clients.Caller.SendAsync("SignalConfirmState", _automation.CurrentSignals);
        await base.OnConnectedAsync();
    }

    /// <summary>前端手动刷新时从 task_records 重取快照，覆盖浏览器显示缓存。</summary>
    public async Task RefreshSnapshot()
    {
        await Clients.Caller.SendAsync("EventsReset", _stages.GetAll());
        await Clients.Caller.SendAsync("ModuleExecLogsReset", new { maxId = 0L, entries = _moduleLogs.GetRecent() });
    }
}
