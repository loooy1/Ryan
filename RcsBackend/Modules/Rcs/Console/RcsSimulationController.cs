using Microsoft.AspNetCore.Mvc;
using Rcs.Contracts.Map;
using Rcs.Contracts.Vehicles;
using RCSBackend.Modules.Rcs.Infrastructure;

namespace RCSBackend.Modules.Rcs.Console;

[ApiController]
[Route("api/rcs")]
public sealed class RcsSimulationController : ControllerBase
{
    private readonly RcsSimulationService _simulation;

    public RcsSimulationController(RcsSimulationService simulation) => _simulation = simulation;

    [HttpGet("map")]
    public ActionResult<GridMapDto> GetMap() => Ok(_simulation.Map);

    [HttpGet("vehicles")]
    public ActionResult<IReadOnlyList<VehicleStateDto>> GetVehicles() =>
        Ok(new[] { _simulation.Vehicle.State });

    [HttpPost("routes/preview")]
    public ActionResult<RouteDto> Preview([FromBody] RunVehicleRequest request) =>
        Ok(_simulation.Preview(request.Start, request.End));

    [HttpPost("vehicles/V-01/run")]
    public async Task<ActionResult<RouteDto>> Run([FromBody] RunVehicleRequest request) =>
        Ok(await _simulation.RunAsync(request.Start, request.End));

    [HttpPost("vehicles/V-01/pause")]
    public IActionResult Pause()
    {
        _simulation.Vehicle.Pause();
        return NoContent();
    }

    [HttpPost("vehicles/V-01/reset")]
    public IActionResult Reset([FromBody] GridPoint position)
    {
        _simulation.Vehicle.Reset(position);
        return NoContent();
    }
}
