using Contracts.Rcs.Map;
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
        return map == null ? NotFound(new { message = $"地图 {mapCode} 不存在。" }) : Ok(map);
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] RcsMapEditorDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            await _maps.SaveAsync(dto, cancellationToken);
            await _cache.ReloadAsync(cancellationToken);
            return Ok(new { success = true, mapCode = dto.MapCode });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }
}
