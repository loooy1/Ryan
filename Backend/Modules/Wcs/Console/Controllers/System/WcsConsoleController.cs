using WCSBackend.Modules.Wcs.Automation.Services;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Console.Services;
using Microsoft.AspNetCore.Mvc;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// WCS 控制台接口（供 WCS 前端调用，不是 GRCS 协议接口）。
/// 前端轮询事件、批准/拒绝进入申请、切换自动放行模式。
/// </summary>
[ApiController]
[Route("api/wcs")]
public class WcsConsoleController : ControllerBase
{
    private readonly ITaskStageService _stages;
    private readonly MockApprovalService _mockApproval;

    public WcsConsoleController(ITaskStageService stages, MockApprovalService mockApproval)
    {
        _stages = stages;
        _mockApproval = mockApproval;
    }

    [HttpGet("status")]
    public ActionResult<object> Status()
    {
        return Ok(new { pendingCount = _mockApproval.PendingCount });
    }

    /// <summary>删除指定任务的全部记录（创建行 + 阶段事件，task_records 全行）。</summary>
    /// <summary>Task board reads the complete task_records database snapshot. SignalR only asks the UI to refresh.</summary>
    [HttpGet("task-records")]
    public ActionResult<object> TaskRecords() => Ok(_stages.GetAll());

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
