using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using RCSBackend.Modules.Rcs.Application.Execution;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs/settings/algorithm")]
public sealed class RcsAlgorithmSettingsController(RcsAlgorithmSettingsService settings) : ControllerBase
{
    [HttpGet]
    public ActionResult<AlgorithmSettingsDto> Get() => Ok(settings.Current);

    [HttpPut]
    public async Task<ActionResult<AlgorithmSettingsDto>> Save([FromBody] AlgorithmSettingsDto value, CancellationToken token)
    {
        try { return Ok(await settings.SaveAsync(value, token)); }
        catch (ArgumentOutOfRangeException ex) { return BadRequest(RcsApiResponse.Rejected(ex.Message)); }
    }
}
