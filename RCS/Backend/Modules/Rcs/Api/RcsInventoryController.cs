using Contracts.Rcs.Inventory;
using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RCSBackend.Modules.Rcs.Application.Inventory;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs/inventory")]
[TypeFilter(typeof(RcsInventoryExceptionFilter))]
public sealed class RcsInventoryController(RcsInventoryCatalogService catalog) : ControllerBase
{
    [HttpGet("models")]
    public async Task<ActionResult<IReadOnlyList<RcsInventoryModelDto>>> GetModels(
        [FromQuery] string? itemType = null, CancellationToken token = default) =>
        Ok(await catalog.GetModelsAsync(itemType, token));

    [HttpPost("models")]
    public async Task<ActionResult<RcsInventoryModelDto>> CreateModel(
        [FromBody] RcsInventoryModelDto dto, CancellationToken token = default)
    {
        var created = await catalog.CreateModelAsync(dto, token);
        return Created($"/api/rcs/inventory/models/{created.Id}", created);
    }

    [HttpPut("models/{id:long}")]
    public async Task<ActionResult<RcsInventoryModelDto>> UpdateModel(long id,
        [FromBody] RcsInventoryModelDto dto, CancellationToken token = default) =>
        Ok(await catalog.UpdateModelAsync(id, dto, token));

    [HttpDelete("models/{id:long}")]
    public async Task<IActionResult> DeleteModel(long id, CancellationToken token = default)
    {
        await catalog.DeleteModelAsync(id, token);
        return Ok(RcsApiResponse.Accepted());
    }

    [HttpGet("instances")]
    public async Task<ActionResult<IReadOnlyList<RcsInventoryInstanceDto>>> GetInstances(
        [FromQuery] string? itemType = null, [FromQuery] string? mapCode = null,
        [FromQuery] string? pointCode = null, [FromQuery] string? parentInstanceCode = null,
        CancellationToken token = default) =>
        Ok(await catalog.GetInstancesAsync(itemType, mapCode, pointCode, parentInstanceCode, token));

    [HttpPost("instances")]
    public async Task<ActionResult<RcsInventoryInstanceDto>> CreateInstance(
        [FromBody] CreateRcsInventoryInstanceRequest request, CancellationToken token = default)
    {
        var created = await catalog.CreateInstanceAsync(request, token);
        return Created($"/api/rcs/inventory/instances/{created.Id}", created);
    }

    [HttpPut("instances/{id:long}/move")]
    public async Task<ActionResult<RcsInventoryInstanceDto>> MoveInstance(long id,
        [FromBody] MoveRcsInventoryInstanceRequest request, CancellationToken token = default) =>
        Ok(await catalog.MoveInstanceAsync(id, request, token));

    [HttpDelete("instances/{id:long}")]
    public async Task<IActionResult> DeleteInstance(long id, CancellationToken token = default)
    {
        await catalog.DeleteInstanceAsync(id, token);
        return Ok(RcsApiResponse.Accepted());
    }
}

/// <summary>只将库存领域错误映射到原有 HTTP 状态和统一响应体。</summary>
public sealed class RcsInventoryExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not RcsInventoryCatalogException error) return;
        var response = RcsApiResponse.Rejected(error.Message);
        context.Result = error.Kind switch
        {
            RcsInventoryErrorKind.Invalid => new BadRequestObjectResult(response),
            RcsInventoryErrorKind.Conflict => new ConflictObjectResult(response),
            RcsInventoryErrorKind.NotFound => new NotFoundObjectResult(response),
            _ => throw new ArgumentOutOfRangeException(nameof(error.Kind))
        };
        context.ExceptionHandled = true;
    }
}
