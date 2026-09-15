using Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;
using WCSBackend.Modules.Wcs.Console.Services;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

[ApiController]
[Route("api/wcs/modules/logs")]
public class ModuleExecutionLogController : ControllerBase
{
    private readonly ModuleExecutionLogStore _logs;

    public ModuleExecutionLogController(ModuleExecutionLogStore logs) => _logs = logs;

    [HttpGet]
    public ActionResult<List<ModuleExecLogEntry>> GetRecent([FromQuery] int take = 200)
        => Ok(_logs.GetRecent(take));
}
