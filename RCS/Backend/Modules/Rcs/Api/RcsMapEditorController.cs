using Contracts.Rcs.Map;
using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using RCSBackend.Modules.Rcs.Application.Maps;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs/editor/map")]
public sealed class RcsMapEditorController : ControllerBase
{
    private readonly RcsMapStore _maps;
    private readonly RcsMapPublicationService _publication;

    public RcsMapEditorController(RcsMapStore maps, RcsMapPublicationService publication) { _maps = maps; _publication = publication; }

    [HttpGet]
    public async Task<ActionResult<RcsMapEditorDto>> Get([FromQuery] string mapCode = "default", CancellationToken cancellationToken = default)
    {
        var map = await _maps.GetAsync(mapCode, cancellationToken);
        return map == null ? NotFound(RcsApiResponse.Rejected($"地图 {mapCode} 不存在。")) : Ok(map);
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] RcsMapEditorDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            await _publication.SaveAsync(dto, cancellationToken);
            return Ok(RcsApiResponse.Accepted());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(RcsApiResponse.Rejected(ex.Message));
        }
    }
}
