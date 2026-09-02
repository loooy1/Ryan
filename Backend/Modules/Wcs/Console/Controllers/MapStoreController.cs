using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 地图缓存接口（/api/wcs/map/*）：
/// 前端 MapReader 解析成功后 POST 上传，其他页面/后端自动化从 GET 取同一份（单一数据源）。
/// </summary>
[ApiController]
[Route("api/wcs/map")]
public class MapStoreController : ControllerBase
{
    private readonly MapStoreService _mapStore;

    public MapStoreController(MapStoreService mapStore) => _mapStore = mapStore;

    [HttpPost("upload")]
    public ActionResult<object> Upload([FromBody] MapUploadDto dto)
    {
        _mapStore.Save(dto);
        return Ok(new { success = true, count = dto.Stations?.Count ?? 0 });
    }

    [HttpGet]
    public ActionResult<object> Get()
    {
        var snap = _mapStore.Snapshot();
        return Ok(snap);
    }
}
