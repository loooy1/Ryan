using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Realtime;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>库存地图接口：地图站点几何与库存状态统一保存在 wcs_slots。</summary>
[ApiController]
[Route("api/wcs/inventory/map")]
public class InventoryMapController : ControllerBase
{
    private readonly WcsMapCatalog _mapCatalog;
    private readonly WcsInventoryStore _inventory;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public InventoryMapController(WcsMapCatalog mapCatalog, WcsInventoryStore inventory,
        IHubContext<TaskStageRealtimeHub> hub)
    {
        _mapCatalog = mapCatalog;
        _inventory = inventory;
        _hub = hub;
    }

    [HttpPost("upload")]
    public ActionResult<object> Upload([FromBody] MapUploadDto dto)
    {
        if (dto?.Stations == null)
            return BadRequest(new { success = false, message = "地图站点列表不能为空" });
        var sync = _inventory.SyncMapSlots(dto.Stations);
        if (!sync.Success)
            return BadRequest(new { success = false, message = sync.Message });
        _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "map");
        return Ok(new { success = true, count = dto.Stations.Count, slotCount = sync.Count });
    }

    [HttpGet]
    public ActionResult<object> Get() => Ok(_mapCatalog.Snapshot());
}
