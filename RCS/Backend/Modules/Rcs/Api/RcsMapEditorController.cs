using Contracts.Rcs.Map;
using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs/editor/map")]
public sealed class RcsMapEditorController : ControllerBase
{
    private readonly RcsMapStore _maps;
    private readonly RcsMapCache _cache;

    public RcsMapEditorController(RcsMapStore maps, RcsMapCache cache) { _maps = maps; _cache = cache; }

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
            await _maps.SaveAsync(dto, cancellationToken);
            await _cache.ReloadAsync(cancellationToken);
            return Ok(RcsApiResponse.Accepted());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(RcsApiResponse.Rejected(ex.Message));
        }
    }
}
