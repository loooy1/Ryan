using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

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
    private readonly ManualInventoryService _manualInventory;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(GrcsInventoryCacheService inventoryCache, WcsInventoryStore invStore,
        ManualInventoryService manualInventory, ILogger<InventoryController> logger)
    {
        _inventoryCache = inventoryCache;
        _invStore = invStore;
        _manualInventory = manualInventory;
        _logger = logger;
    }

    /// <summary>WCS 储位快照，供库存管理地图显示本地库存、锁与选点状态。</summary>
    [HttpGet("slots")]
    public ActionResult<List<Contracts.Entities.WcsSlotRow>> Slots() => Ok(_invStore.Slots());

    /// <summary>
    /// WCS 指定储位手工入库：仅纯货物，先写入 WCS 储位表，再按站点顺序逐条调用 RCS /api/Cargo/Enter。
    /// 托盘由 RCS /AutoContainerEnter 批量创建后，再通过同步接口回写 WCS。
    /// </summary>
    [HttpPost("manual-enter")]
    public async Task<ActionResult<ManualInventoryEnterResult>> ManualEnter([FromBody] ManualInventoryEnterRequest request)
    {
        if (request == null) return BadRequest(new ManualInventoryEnterResult { Message = "请求不能为空" });
        var result = await _manualInventory.EnterAsync(request);
        return result.WcsWritten ? Ok(result) : BadRequest(result);
    }

    /// <summary>打开库存地图时读取一次 RCS 在途托盘坐标，不启动后台轮询。</summary>
    [HttpGet("transit-pallet-positions")]
    public async Task<ActionResult<List<TransitPalletPositionDto>>> TransitPalletPositions()
        => Ok(await _manualInventory.GetTransitPalletPositionsAsync());

    /// <summary>同步库存账本：清空重建，以 GRCS 为准。有在途自动化任务时拒绝（避免搬动中数据打架）。</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<object>> Sync()
    {
        if (_invStore.HasInTransit())
            return BadRequest(new { success = false, message = "存在在途自动化任务，禁止同步库存；请先停止或等待任务完成" });
        _invStore.EnsureSlots();
        var records = await _inventoryCache.SyncAllAsync();
        if (!_inventoryCache.Ready && records.Count == 0)
            return BadRequest(new { success = false, message = "GRCS 未响应，无法同步库存" });
        _invStore.RebuildFromGrcs(records);
        _invStore.SetSyncedMarks(new HashSet<string>());
        var slots = _invStore.Slots();
        var occupied = slots.Count(s => !string.IsNullOrEmpty(s.PalletCode) || !string.IsNullOrEmpty(s.CargoCode));
        var loaded = slots.Count(s => !string.IsNullOrEmpty(s.PalletCode) && !string.IsNullOrEmpty(s.CargoCode));
        var emptyPallets = slots.Count(s => !string.IsNullOrEmpty(s.PalletCode) && string.IsNullOrEmpty(s.CargoCode));
        var cargos = slots.Count(s => string.IsNullOrEmpty(s.PalletCode) && !string.IsNullOrEmpty(s.CargoCode));
        _logger.LogInformation("库存同步完成：GRCS {Count} 条 → 储位表 {Slots}（占用 {Occupied}：带货托 {Loaded}、空托 {Empty}、纯货物 {Cargo}）",
            records.Count, slots.Count, occupied, loaded, emptyPallets, cargos);
        return Ok(new
        {
            success = true,
            syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            grcsCount = records.Count,
            slots = slots.Count,
            occupied = occupied,
            loaded = loaded,
            emptyPallets = emptyPallets,
            cargos = cargos,
        });
    }
}
