using Contracts.Rcs.Route;
using Contracts.Rcs.Vehicle;
using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using RCSBackend.Modules.Rcs.Application.Simulation;
using RCSBackend.Modules.Rcs.Application.Tasks;
using RCSBackend.Modules.Rcs.Application.Vehicles;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs/vehicles")]
public sealed class RcsVehiclesController(RcsVehicleService vehicles, IRcsTaskService tasks,
    RcsSimulationService simulation) : ControllerBase
{
    [HttpGet("protocols")]
    public async Task<ActionResult<IReadOnlyList<VehicleProtocolInfoDto>>> Protocols(CancellationToken token)
    {
        await vehicles.ListAsync(token);
        return Ok(vehicles.SupportedProtocols);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<VehicleStateDto>>> List(CancellationToken token)
    {
        await vehicles.ListAsync(token);
        return Ok(simulation.Vehicles);
    }

    [HttpPost("{id}/heartbeat")]
    [Consumes("application/json", "application/octet-stream", "text/plain")]
    public async Task<ActionResult<VehicleStateDto>> Heartbeat(string id, CancellationToken token)
    {
        const int MaxPayloadBytes = 64 * 1024;
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await Request.Body.ReadAsync(chunk, token);
            if (count == 0) break;
            if (buffer.Length + count > MaxPayloadBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, Error("心跳报文不能超过 64 KB。"));
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        if (buffer.Length == 0) return BadRequest(Error("心跳报文不能为空。"));
        try { return Ok(await vehicles.ReceiveHeartbeatAsync(id, buffer.ToArray(), token)); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
    }

    [HttpPost]
    public async Task<ActionResult<VehicleStateDto>> Add(CreateVehicleRequest request, CancellationToken token)
    {
        try { var state = await vehicles.AddAsync(request, token); state = Current(state.Id) ?? state; return Created($"/api/rcs/vehicles/{state.Id}", state); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<VehicleStateDto>> Get(string id, CancellationToken token) =>
        await GetCurrentAsync(id, token) is { } state ? Ok(state) : NotFound(Error("车辆不存在。"));

    [HttpPut("{id}")]
    public async Task<ActionResult<VehicleStateDto>> Update(string id, UpdateVehicleRequest request, CancellationToken token)
    {
        try { await vehicles.UpdateAsync(id, request, token); return Current(id) is { } state ? Ok(state) : NotFound(Error("车辆不存在。")); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken token)
    {
        try { await vehicles.DeleteAsync(id, token); return Ok(RcsApiResponse.Accepted()); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpPost("{id}/run")]
    public async Task<ActionResult<RouteDto>> Run(string id, RunVehicleRequest request, CancellationToken token)
    {
        var state = await GetCurrentAsync(id, token);
        if (state is null) return NotFound(Error("车辆不存在。"));
        if (!state.IsEnabled) return Conflict(Error("车辆已停用，请先启用。"));
        try { return Ok(await simulation.RunAsync(request.StartPointCode, request.EndPointCode, state.Id, token)); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }

    [HttpPost("{id}/pause")]
    public Task<IActionResult> Pause(string id, CancellationToken token) => Control(id, () => tasks.PauseVehicleAsync(id, token), token);
    [HttpPost("{id}/resume")]
    public Task<IActionResult> Resume(string id, CancellationToken token) => Control(id, () => tasks.ResumeVehicleAsync(id, token), token);
    [HttpPost("{id}/stop")]
    public Task<IActionResult> Stop(string id, CancellationToken token) => Control(id, () => tasks.StopVehicleAsync(id, token), token);

    [HttpPost("{id}/reset")]
    public async Task<IActionResult> Reset(string id, [FromBody] string pointCode, CancellationToken token)
    {
        try { await vehicles.ResetAsync(id, pointCode, token); return Ok(RcsApiResponse.Accepted()); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }
    [HttpPut("{id}/position")]
    public async Task<ActionResult<VehicleStateDto>> SetPosition(string id, [FromBody] string pointCode, CancellationToken token)
    {
        try { await vehicles.SetPositionAsync(id, pointCode, token); return Current(id) is { } state ? Ok(state) : NotFound(Error("车辆不存在。")); }
        catch (ArgumentException ex) { return BadRequest(Error(ex.Message)); }
        catch (RcsTaskConflictException ex) { return Conflict(Error(ex.Message)); }
    }
    private async Task<IActionResult> Control(string id, Func<Task<bool>> action, CancellationToken token)
    {
        if (!(await vehicles.ListAsync(token)).Any(x => SameId(x.Id, id))) return NotFound(Error("车辆不存在。"));
        return await action() ? Ok(RcsApiResponse.Accepted()) : Conflict(Error("该车辆没有符合操作条件的活动任务。"));
    }
    private async Task<VehicleStateDto?> GetCurrentAsync(string id, CancellationToken token)
    {
        await vehicles.ListAsync(token);
        return Current(id);
    }
    private VehicleStateDto? Current(string id) => simulation.Vehicles.FirstOrDefault(x => SameId(x.Id, id));
    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static RcsApiResponse Error(string message) => RcsApiResponse.Rejected(message);
}
