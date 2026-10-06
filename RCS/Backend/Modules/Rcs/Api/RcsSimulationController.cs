using Microsoft.AspNetCore.Mvc;
using Contracts.Rcs.Map;
using Contracts.Rcs.Route;
using Contracts.Rcs.Tasks;
using Contracts.Rcs.Vehicle;
using RCSBackend.Modules.Rcs.Application.Simulation;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs")]
public sealed class RcsSimulationController : ControllerBase
{
    private readonly RcsSimulationService _simulation;

    public RcsSimulationController(RcsSimulationService simulation)
    { _simulation = simulation; }

    [HttpGet("map")]
    public ActionResult<RcsMapSnapshot> GetMap() => _simulation.Map is { } map ? Ok(map) : NotFound(RcsApiResponse.Rejected("当前没有已加载地图。"));

    [HttpPost("map/reload")]
    public async Task<ActionResult<RcsMapSnapshot>> ReloadMap(CancellationToken cancellationToken = default)
    {
        var map = await _simulation.ReloadMapAsync(cancellationToken);
        return map is null ? NotFound(RcsApiResponse.Rejected("没有可加载的地图。")) : Ok(map);
    }

    [HttpPost("routes/preview")]
    public ActionResult<RouteDto> Preview([FromBody] RunVehicleRequest request) =>
        Ok(_simulation.Preview(request.StartPointCode, request.EndPointCode));

}
