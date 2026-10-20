using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using RCSBackend.Modules.Rcs.Application.Tasks;
using System.Text.Json;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
public sealed class RcsTasksController(IRcsTaskService tasks, IRcsTaskQueryService queries,
    ILogger<RcsTasksController> logger) : ControllerBase
{
    [HttpPost("/api/v1/task_receive")]
    public async Task<ActionResult<RcsApiResponse>> Receive(CancellationToken token)
    {
        logger.LogInformation("收到上游任务请求 POST /api/v1/task_receive");
        string originalRequestJson;
        try
        {
            using var payload = await JsonDocument.ParseAsync(Request.Body, cancellationToken: token);
            originalRequestJson = payload.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            return Ok(Error($"请求 JSON 格式无效：{ex.Message}"));
        }

        RcsUpstreamTaskReceiveRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RcsUpstreamTaskReceiveRequest>(
                originalRequestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            return Ok(Error($"请求 JSON 格式无效：{ex.Message}"));
        }
        if (request is null) return Ok(Error("请求正文不能为空。"));

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
        try
        {
            var response = await tasks.ReceiveAsync(normalized, originalRequestJson, token);
            if (!response.Success && string.IsNullOrWhiteSpace(response.Exception))
            {
                const string message = "任务接收失败：RCS 未返回具体异常信息，请检查任务校验及服务端日志。";
                logger.LogError("任务接收服务返回 success=false 但未提供 Exception，GroupId={GroupId}", request.GroupId);
                response = RcsApiResponse.Rejected(message);
            }

            logger.LogInformation("上游任务请求处理完成，GroupId={GroupId} Success={Success} Exception={Exception}",
                request.GroupId, response.Success, response.Exception);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "上游任务校验失败，GroupId={GroupId}", request.GroupId);
            return Ok(Error(ex.Message));
        }
        catch (RcsTaskConflictException ex)
        {
            logger.LogWarning(ex, "上游任务冲突，GroupId={GroupId}", request.GroupId);
            return Ok(Error(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "上游任务接收失败，GroupId={GroupId}", request.GroupId);
            return Ok(Error($"任务接收失败：{ex.Message}"));
        }
    }

    [HttpPost("/api/rcs/tasks/manual")]
    public async Task<ActionResult<RcsApiResponse>> ReceiveManual([FromBody] RcsTaskReceiveRequest request, CancellationToken token)
    {
        try { return Ok(await tasks.ReceiveManualAsync(request, token)); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpGet("/api/rcs/tasks")]
    public async Task<ActionResult<IReadOnlyList<RcsTaskDto>>> List([FromQuery] int limit = 100, CancellationToken token = default) =>
        Ok(await queries.ListAsync(limit, token));

    [HttpGet("/api/rcs/tasks/page")]
    public async Task<ActionResult<RcsTaskPageDto>> ListPage([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string status = "", [FromQuery] string search = "", CancellationToken token = default) =>
        Ok(await queries.ListPageAsync(page, pageSize, status, search, token));

    [HttpDelete("/api/rcs/tasks/all")]
    public async Task<ActionResult<RcsTaskClearResultDto>> ClearAll(CancellationToken token)
    {
        try { return Ok(new RcsTaskClearResultDto(await tasks.ClearAllAsync(token))); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpGet("/api/rcs/tasks/{taskId}")]
    public async Task<ActionResult<RcsTaskDto>> Get(string taskId, CancellationToken token) =>
        await queries.GetAsync(taskId, token) is { } task ? Ok(task) : NotFound(Error("任务不存在。"));

    [HttpPost("/api/rcs/tasks/{taskId}/pause")]
    public async Task<IActionResult> Pause(string taskId, CancellationToken token) =>
        await tasks.PauseAsync(taskId, token) ? Ok(RcsApiResponse.Accepted()) : Conflict(Error("任务不在执行或暂停状态。"));

    [HttpPost("/api/rcs/tasks/{taskId}/resume")]
    public async Task<IActionResult> Resume(string taskId, CancellationToken token) =>
        await tasks.ResumeAsync(taskId, token) ? Ok(RcsApiResponse.Accepted()) : Conflict(Error("任务不在暂停状态。"));

    [HttpPost("/api/rcs/tasks/{taskId}/cancel")]
    public async Task<IActionResult> Cancel(string taskId, CancellationToken token) =>
        await tasks.CancelAsync(taskId, token) ? Ok(RcsApiResponse.Accepted()) : Conflict(Error("任务不存在或已结束。"));

    [HttpPost("/api/rcs/tasks/{taskId}/replan")]
    public async Task<ActionResult<RcsTaskDto>> Replan(string taskId, CancellationToken token)
    {
        try { return Ok(await tasks.ReplanAsync(taskId, token)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    private static RcsApiResponse Error(string message) => RcsApiResponse.Rejected(message);
}
