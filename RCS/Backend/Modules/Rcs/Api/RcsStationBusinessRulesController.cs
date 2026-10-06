using Contracts.Rcs.StationBusiness;
using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using RCSBackend.Modules.Rcs.Application.StationBusiness;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs/station-business-rules")]
public sealed class RcsStationBusinessRulesController(RcsStationBusinessRuleService rules) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<RcsStationBusinessRuleDto>> Get([FromQuery] string mapCode = "default", [FromQuery] string? pointCode = null) => Ok(rules.Get(mapCode, pointCode));

    [HttpPost]
    public async Task<ActionResult<RcsStationBusinessRuleDto>> Create([FromBody] SaveRcsStationBusinessRuleRequest value, CancellationToken token)
    {
        try { return Ok(await rules.SaveAsync(0, value, token)); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { return BadRequest(RcsApiResponse.Rejected(ex.Message)); }
    }

    [HttpPut("{id:long}")]
    public async Task<ActionResult<RcsStationBusinessRuleDto>> Update(long id, [FromBody] SaveRcsStationBusinessRuleRequest value, CancellationToken token)
    {
        try { return Ok(await rules.SaveAsync(id, value, token)); }
        catch (KeyNotFoundException ex) { return NotFound(RcsApiResponse.Rejected(ex.Message)); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { return BadRequest(RcsApiResponse.Rejected(ex.Message)); }
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken token)
    {
        try { await rules.DeleteAsync(id, token); return NoContent(); }
        catch (KeyNotFoundException ex) { return NotFound(RcsApiResponse.Rejected(ex.Message)); }
    }
}
