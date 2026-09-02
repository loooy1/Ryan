using GrcsBackend.Modules.Wcs.Automation.Services;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Console.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// WCS 控制台接口（供 WCS 前端调用，不是 GRCS 协议接口）。
/// 前端轮询事件、批准/拒绝进入申请、切换自动放行模式。
/// </summary>
[ApiController]
[Route("api/wcs")]
public class WcsConsoleController : ControllerBase
{
    private readonly ITaskStageService _stages;
    private readonly SignalAutoHostedService _signals;
    private readonly MockApprovalService _mockApproval;

    public WcsConsoleController(ITaskStageService stages, SignalAutoHostedService signals, MockApprovalService mockApproval)
    {
        _stages = stages;
        _signals = signals;
        _mockApproval = mockApproval;
    }

    /// <summary>当前状态：是否自动模式 + 待确认数（自动模式读统一信号源 SignalAutoHostedService）。</summary>
    [HttpGet("status")]
    public ActionResult<object> Status()
    {
        return Ok(new { pendingCount = _mockApproval.PendingCount });
    }

    /// <summary>删除指定任务的全部记录（创建行 + 阶段事件，task_records 全行）。</summary>
    [HttpDelete("task-stages/{taskId}")]
    public ActionResult<object> DeleteTaskStages(string taskId)
    {
        _stages.RemoveByTaskId(taskId);
        return Ok(new { success = true, taskId });
    }

    [HttpPost("mock-approvals/decisions/{key}")]
    public ActionResult<object> DecideMock(string key, [FromBody] DecisionRequest request)
    {
        _mockApproval.Decide(key, request.Allow);
        return Ok(new { success = true, key, allow = request.Allow });
    }

    [HttpDelete("mock-approvals/{key}")]
    public ActionResult<object> DeleteMockApproval(string key)
    {
        _mockApproval.RemoveEvent(key);
        return Ok(new { success = true, key });
    }

    [HttpDelete("mock-approvals")]
    public ActionResult<object> ClearMockApprovals()
    {
        _mockApproval.ClearAll();
        return Ok(new { success = true });
    }
}

/// <summary>批准/拒绝请求体（准入 + 通用 Mock 审批共用）。</summary>
public class DecisionRequest
{
    public bool Allow { get; set; }
}
