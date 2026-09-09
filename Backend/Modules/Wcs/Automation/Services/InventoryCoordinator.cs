using WCSBackend.Modules.Wcs.Infrastructure;
using InvItem = WCSBackend.Modules.Wcs.Infrastructure.WcsInventoryStore.InvItem;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>WCS 双表库存协调器：选池构建 / 库存统计 / 储位占用计算（容器表 + 储位表）。</summary>
public class InventoryCoordinator
{
    private readonly WcsInventoryStore _invStore;
    private readonly RangeConfigService _range;
    private readonly MapStoreService _map;
    private readonly AutomationLogService _log;

    public InventoryCoordinator(WcsInventoryStore invStore,
        RangeConfigService range, MapStoreService map, AutomationLogService log)
    {
        _invStore = invStore; _range = range;
        _map = map; _log = log;
    }

    /// <summary>从容器表+储位表构建本轮选池（范围过滤 + 仅储位 + 排除非 ready）。
    /// 自动化只做容器状态流转，不拉取 GRCS 库存写库（库存只在手动「同步」时以 GRCS 为准重建）。</summary>
    public async Task<(List<InvItem> Empty, List<InvItem> Loaded, List<InvItem> Cargo, bool Ok,
        int Stored, int LockedStored, int LoadedStored, DateTime SnapshotAt)> SnapshotAsync(string roundId)
    {
        try
        {
            var range = _range.Get();
            var rangeSet = (range.Enabled && range.Marks.Count > 0)
                ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase)
                : null;
            var (empty, loaded, cargo, stored, lockedStored, loadedStored) =
                _invStore.BuildPool(rangeSet, _invStore.StorageMarks());
            return (empty, loaded, cargo, true, stored, lockedStored, loadedStored, DateTime.Now);
        }
        catch (Exception ex)
        {
            _log.Add(roundId, $"库存查询异常：{ex.Message}", "#f87171");
            return (new List<InvItem>(), new List<InvItem>(), new List<InvItem>(), false, 0, 0, 0, DateTime.MinValue);
        }
    }

    /// <summary>库存统计（储位表维度）：锁定中 = 非 ready 且非 transit 状态单元（picked 在储位/fail）；
    /// 在途 = transit（已取走，号码保留作出发记录）单独区块；
    /// 同一储位托盘+货物均在锁定/在途 → 合并一行（Code=托盘号, CargoCode=货物号）；
    /// 空托/带货托/纯货物 = ready 储位行分类（有托有货=带货托；有货无托=纯货物；有托无货=空托）。</summary>
    public Task<InventorySummaryDto> GetInventorySummaryAsync()
    {
        var dto = new InventorySummaryDto();
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase) : null;
        var storageMarks = _invStore.StorageMarks();

        var slots = _invStore.Slots();
        foreach (var s in slots)
        {
            if (rangeSet != null && !rangeSet.Contains(s.Mark)) continue;
            if (!storageMarks.Contains(s.Mark)) continue;
            var palletTransit = !string.IsNullOrEmpty(s.PalletCode) && s.PalletStatus == "transit";
            var cargoTransit = !string.IsNullOrEmpty(s.CargoCode) && s.CargoStatus == "transit";
            var palletLocked = !string.IsNullOrEmpty(s.PalletCode) && s.PalletStatus is "picked" or "fail";
            var cargoLocked = !string.IsNullOrEmpty(s.CargoCode) && s.CargoStatus is "picked" or "fail";

            if (palletTransit || cargoTransit)
            {
                if (palletTransit && cargoTransit)
                    dto.TransitItems.Add(new InventoryDetailItem { Code = s.PalletCode, Station = s.Mark, CargoCode = s.CargoCode, Status = s.PalletStatus });
                else if (palletTransit)
                    dto.TransitItems.Add(new InventoryDetailItem { Code = s.PalletCode, Station = s.Mark, Status = s.PalletStatus });
                else
                    dto.TransitItems.Add(new InventoryDetailItem { Code = s.CargoCode, Station = s.Mark, Status = s.CargoStatus });
                continue;
            }
            if (palletLocked || cargoLocked)
            {
                if (palletLocked && cargoLocked)
                    dto.LockedItems.Add(new InventoryDetailItem { Code = s.PalletCode, Station = s.Mark, CargoCode = s.CargoCode, Status = s.PalletStatus });
                else if (palletLocked)
                    dto.LockedItems.Add(new InventoryDetailItem { Code = s.PalletCode, Station = s.Mark, Status = s.PalletStatus });
                else
                    dto.LockedItems.Add(new InventoryDetailItem { Code = s.CargoCode, Station = s.Mark, Status = s.CargoStatus });
                continue;
            }

            if (!string.IsNullOrEmpty(s.CargoCode))
            {
                if (string.IsNullOrEmpty(s.PalletCode))
                {
                    dto.Cargo++;
                    dto.CargoItems.Add(new InventoryDetailItem { Code = s.CargoCode, Station = s.Mark });
                }
                else
                {
                    dto.Loaded++;
                    dto.LoadedItems.Add(new InventoryDetailItem { Code = s.PalletCode, Station = s.Mark, CargoCode = s.CargoCode });
                }
            }
            else if (!string.IsNullOrEmpty(s.PalletCode))
            {
                dto.Empty++;
                dto.EmptyItems.Add(new InventoryDetailItem { Code = s.PalletCode, Station = s.Mark });
            }
        }
        dto.Locked = dto.LockedItems.Count;
        dto.Transit = dto.TransitItems.Count;
        return Task.FromResult(dto);
    }

    /// <summary>从储位表重建终点选点占用集合：号码存在且非 transit 的储位（transit=已取走，物理空，可再选为终点）。</summary>
    public HashSet<string> BuildOccupied()
    {
        var storageMarks = new HashSet<string>(
            _map.GetStations().Where(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0).Select(s => s.Mark),
            StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _invStore.Slots())
        {
            if (!storageMarks.Contains(s.Mark)) continue;
            var occupied = (!string.IsNullOrEmpty(s.PalletCode) && s.PalletStatus != "transit")
                || (!string.IsNullOrEmpty(s.CargoCode) && s.CargoStatus != "transit");
            if (occupied)
                set.Add(s.Mark);
        }
        return set;
    }
}