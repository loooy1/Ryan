using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// WCS 库存账本接口（/api/wcs/inventory/*）。
/// 同步 = 删除账本全部记录 → 拉 GRCS 全量库存以 GRCS 为准重建（有在途任务时禁止）。
/// </summary>
[ApiController]
[Route("api/wcs/inventory")]
public class InventoryController : ControllerBase
{
    private readonly GrcsInventoryCacheService _inventoryCache;
    private readonly WcsInventoryStore _invStore;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(GrcsInventoryCacheService inventoryCache, WcsInventoryStore invStore, ILogger<InventoryController> logger)
    {
        _inventoryCache = inventoryCache;
        _invStore = invStore;
        _logger = logger;
    }

    /// <summary>同步库存账本：清空重建，以 GRCS 为准。有在途自动化任务时拒绝（避免搬动中数据打架）。</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<object>> Sync()
    {
        if (_invStore.HasInTransit())
            return BadRequest(new { success = false, message = "存在在途自动化任务，禁止同步库存；请先停止或等待任务完成" });
        var records = await _inventoryCache.SyncAllAsync();
        if (!_inventoryCache.Ready && records.Count == 0)
            return BadRequest(new { success = false, message = "GRCS 未响应，无法同步库存" });
        _invStore.RebuildFromGrcs(records);
        _invStore.SetSyncedMarks(new HashSet<string>());
        _logger.LogInformation("库存账本已同步重建：{Count} 条（以 GRCS 为准）", records.Count);
        return Ok(new { success = true, count = records.Count });
    }
}