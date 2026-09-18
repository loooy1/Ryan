using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Realtime;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 地图缓存接口（/api/wcs/map/*）：
/// 前端 MapReader 解析成功后 POST 上传，其他页面/后端自动化从 GET 取同一份（单一数据源）。
/// </summary>
[ApiController]
[Route("api/wcs/map")]
public class MapStoreController : ControllerBase
{
    private readonly MapStoreService _mapStore;
    private readonly WcsInventoryStore _invStore;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public MapStoreController(MapStoreService mapStore, WcsInventoryStore invStore, IHubContext<TaskStageRealtimeHub> hub)
    {
        _mapStore = mapStore;
        _invStore = invStore;
        _hub = hub;
    }

    [HttpPost("upload")]
    public ActionResult<object> Upload([FromBody] MapUploadDto dto)
    {
        if (_invStore.HasInTransit())
            return BadRequest(new { success = false, message = "存在在途自动化任务，禁止更新地图（任务依赖储位坐标）；请先停止或等待任务完成" });
        _mapStore.Save(dto);
        var slotCount = _invStore.SyncMapSlots();
        _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "map");
        return Ok(new { success = true, count = dto.Stations?.Count ?? 0, slotCount });
    }

    [HttpGet]
    public ActionResult<object> Get()
    {
        var snap = _mapStore.Snapshot();
        return Ok(snap);
    }
}
