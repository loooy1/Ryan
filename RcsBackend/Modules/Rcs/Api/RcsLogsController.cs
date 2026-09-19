using Backend.Shared.Logging;
using Microsoft.AspNetCore.Mvc;

namespace RCSBackend.Modules.Rcs.Console;

[ApiController]
[Route("api/rcs/logs")]
public sealed class RcsLogsController(LogEventBuffer buffer) : ControllerBase
{
    [HttpGet("recent")]
    public ActionResult<IReadOnlyList<AppLogEvent>> Recent(
        string? category = null, string? component = null, string? taskId = null, int take = 200)
        => Ok(buffer.GetRecent(category, component, taskId, take));
}
