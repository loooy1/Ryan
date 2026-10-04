using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using RCSBackend.Modules.Rcs.Application.Tasks;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
public sealed class RcsTasksController(IRcsTaskService tasks) : ControllerBase
{
    [HttpPost("/api/v1/task_receive")]
    public async Task<ActionResult<RcsTaskReceiveResponse>> Receive([FromBody] RcsUpstreamTaskReceiveRequest request, CancellationToken token)
    {
        var normalized = new RcsTaskReceiveRequest
        {
            GroupId = request.GroupId,
            MsgTime = request.MsgTime,
            PriorityCode = request.PriorityCode,
            Warehouse = request.Warehouse,
            Tasks = request.Tasks?.Select(task => task is null ? null! : new RcsTaskRequest
            {
                TaskId = task.TaskId,
                TaskType = task.TaskType,
                ContainerCode = task.ContainerCode,
                StationCode = task.StationCode,
                AreaCode = task.AreaCode
            }).ToList() ?? []
        };
        try { return Ok(await tasks.ReceiveAsync(normalized, token)); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpPost("/api/rcs/tasks/manual")]
    public async Task<ActionResult<RcsTaskReceiveResponse>> ReceiveManual([FromBody] RcsTaskReceiveRequest request, CancellationToken token)
    {
        try { return Ok(await tasks.ReceiveManualAsync(request, token)); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpGet("/api/rcs/tasks")]
    public async Task<ActionResult<IReadOnlyList<RcsTaskDto>>> List([FromQuery] int limit = 100, CancellationToken token = default) =>
        Ok(await tasks.ListAsync(limit, token));

    [HttpGet("/api/rcs/tasks/{taskId}")]
    public async Task<ActionResult<RcsTaskDto>> Get(string taskId, CancellationToken token) =>
        await tasks.GetAsync(taskId, token) is { } task ? Ok(task) : NotFound();

    [HttpPost("/api/rcs/tasks/{taskId}/pause")]
    public async Task<IActionResult> Pause(string taskId, CancellationToken token) =>
        await tasks.PauseAsync(taskId, token) ? NoContent() : Conflict(Error("任务不在执行或暂停状态。"));

    [HttpPost("/api/rcs/tasks/{taskId}/resume")]
    public async Task<IActionResult> Resume(string taskId, CancellationToken token) =>
        await tasks.ResumeAsync(taskId, token) ? NoContent() : Conflict(Error("任务不在暂停状态。"));

    [HttpPost("/api/rcs/tasks/{taskId}/cancel")]
    public async Task<IActionResult> Cancel(string taskId, CancellationToken token) =>
        await tasks.CancelAsync(taskId, token) ? NoContent() : Conflict(Error("任务不存在或已结束。"));

    [HttpPost("/api/rcs/tasks/{taskId}/replan")]
    public async Task<ActionResult<RcsTaskDto>> Replan(string taskId, CancellationToken token)
    {
        try { return Ok(await tasks.ReplanAsync(taskId, token)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    private static RcsTaskReceiveResponse Error(string message) => new()
        { Success = false, Message = message, MsgTime = RcsTaskService.ProtocolTime() };
}
