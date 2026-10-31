using WCSBackend.Modules.Wcs.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Console.Services;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

[ApiController]
[Route("api/wcs/inventory")]
public class InventoryController : ControllerBase
{
    private readonly WcsInventoryStore _invStore;
    private readonly TransitPalletPositionService _transitPallets;

    public InventoryController(WcsInventoryStore invStore, TransitPalletPositionService transitPallets)
    {
        _invStore = invStore;
        _transitPallets = transitPallets;
    }

    [HttpGet("slots")]
    public ActionResult<List<Contracts.Entities.WcsSlotRow>> Slots() => Ok(_invStore.Slots());

    [HttpPost("sorting-association")]
    public ActionResult<SortingStationAssociationResult> SaveSortingAssociation(
        [FromBody] SortingStationAssociationRequest request)
    {
        if (request == null) return BadRequest(new SortingStationAssociationResult { Message = "请求不能为空" });
        var result = _invStore.SaveSortingAssociation(request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("units")]
    public ActionResult<InventoryUnitMutationResult> AddUnit([FromBody] InventoryUnitMutationRequest request)
    {
        if (request == null) return BadRequest(new InventoryUnitMutationResult { Message = "请求不能为空" });
        var result = _invStore.AddUnit(request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("units/{mark}/{kind}")]
    public ActionResult<InventoryUnitMutationResult> RemoveUnit(string mark, string kind)
    {
        var result = _invStore.RemoveUnit(mark, kind);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("units/generate-all")]
    public ActionResult<InventoryBatchMutationResult> GenerateAllUnits([FromBody] InventoryBatchMutationRequest request)
    {
        if (request == null) return BadRequest(new InventoryBatchMutationResult { Message = "请求不能为空" });
        var result = _invStore.GenerateAllUnits(request);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("units/all/{kind}")]
    public ActionResult<InventoryBatchMutationResult> RemoveAllUnits(string kind)
    {
        var result = _invStore.RemoveAllUnits(kind);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("transit-pallet-positions")]
    public async Task<ActionResult<List<TransitPalletPositionDto>>> TransitPalletPositions()
        => Ok(await _transitPallets.GetTransitPalletPositionsAsync());
}
