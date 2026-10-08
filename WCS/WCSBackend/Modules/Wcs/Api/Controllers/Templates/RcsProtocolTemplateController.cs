using Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Models;
using WCSBackend.Modules.Wcs.Realtime;

namespace WCSBackend.Modules.Wcs.Api.Controllers.Templates;

[ApiController]
[Route("api/wcs/rcs-profiles")]
public sealed class RcsProtocolTemplateController : ControllerBase
{
    private readonly RcsProtocolTemplateStore _store;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public RcsProtocolTemplateController(RcsProtocolTemplateStore store, IHubContext<TaskStageRealtimeHub> hub)
    {
        _store = store;
        _hub = hub;
    }

    [HttpGet]
    public ActionResult<object> GetAll() => Ok(new { success = true, data = _store.GetAll() });

    [HttpPut]
    public ActionResult<object> Save([FromBody] RcsProtocolProfilesDto? config)
    {
        if (config == null) return BadRequest(new { success = false, message = "RCS 配置内容为空。" });
        try
        {
            var saved = _store.Save(config);
            _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "rcs-profiles");
            return Ok(new { success = true, data = saved });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("activate/{profileId}")]
    public ActionResult<object> Activate([FromRoute] string profileId)
    {
        try
        {
            var saved = _store.Activate(profileId);
            _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "rcs-profiles");
            return Ok(new { success = true, data = saved });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
    }

    [HttpDelete("{profileId}")]
    public ActionResult<object> Delete([FromRoute] string profileId)
    {
        try
        {
            var saved = _store.Delete(profileId);
            _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "rcs-profiles");
            return Ok(new { success = true, data = saved });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }
}
