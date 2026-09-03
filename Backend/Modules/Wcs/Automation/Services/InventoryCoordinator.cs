using GrcsBackend.Modules.Wcs.Infrastructure;
using InvItem = GrcsBackend.Modules.Wcs.Infrastructure.WcsInventoryStore.InvItem;
using GrcsBackend.Contracts.Dtos;

namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>WCS 库存账本协调器：选池构建 / 库存统计 / 储位占用计算。</summary>
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

    /// <summary>从 WCS 库存账本构建本轮选池（范围过滤 + 仅储位 + 排除 busy/picked）。
    /// 自动化只做账本状态流转，不拉取 GRCS 库存写库（库存只在手动「同步库存」时以 GRCS 为准重建）。</summary>
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

    /// <summary>从 WCS 库存账本统计 + 明细（不拉 GRCS，与选池 BuildPool 同源同规则，保证「显示」与「下发」一致）。</summary>
    public Task<InventorySummaryDto> GetInventorySummaryAsync()
    {
        var dto = new InventorySummaryDto();
        var range = _range.Get();
        var rangeSet = (range.Enabled && range.Marks.Count > 0)
            ? new HashSet<string>(range.Marks, StringComparer.OrdinalIgnoreCase) : null;
        var storageMarks = _invStore.StorageMarks();

        var rows = _invStore.All().Where(r =>
            (rangeSet == null || rangeSet.Contains(r.Station))
            && storageMarks.Contains(r.Station)).ToList();

        var containerStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cargoStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cargoByStation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (r.Code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
            {
                cargoStations.Add(r.Station);
                if (!string.IsNullOrEmpty(r.Station) && !cargoByStation.ContainsKey(r.Station))
                    cargoByStation[r.Station] = r.Code;
            }
            else if (r.Code.Contains("Container", StringComparison.OrdinalIgnoreCase))
                containerStations.Add(r.Station);
        }

        var lockedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in rows.Where(r => r.Status == "busy" && !string.IsNullOrEmpty(r.TaskId)
            && !r.TaskId.StartsWith(WcsInventoryStore.FailPrefix, StringComparison.OrdinalIgnoreCase)
            && !r.TaskId.StartsWith(WcsInventoryStore.GrcsLockPrefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase))
        {
            var grp = g.ToList();
            var pallet = grp.FirstOrDefault(r => r.Code.Contains("Container", StringComparison.OrdinalIgnoreCase));
            var main = pallet?.Code ?? grp.FirstOrDefault(r => !string.IsNullOrEmpty(r.Code))?.Code ?? "";
            var cargoCode = grp.FirstOrDefault(r => r.Code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))?.Code;
            if (string.IsNullOrEmpty(main)) continue;
            dto.LockedItems.Add(new InventoryDetailItem { Code = main, CargoCode = cargoCode, Station = grp.FirstOrDefault(r => r.Code == main)?.Station });
            lockedCodes.Add(main);
        }
        foreach (var r in rows.Where(x => x.Status == "picked"))
        {
            dto.LockedItems.Add(new InventoryDetailItem { Code = r.Code, Station = r.Station });
            lockedCodes.Add(r.Code);
        }
        dto.Locked = lockedCodes.Count;

        foreach (var r in rows)
        {
            if (r.Status != "idle") continue;
            if (lockedCodes.Contains(r.Code)) continue;
            if (r.Code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
            {
                if (!containerStations.Contains(r.Station))
                {
                    dto.Cargo++;
                    dto.CargoItems.Add(new InventoryDetailItem { Code = r.Code, Station = r.Station });
                }
            }
            else if (r.Code.Contains("Container", StringComparison.OrdinalIgnoreCase))
            {
                if (cargoStations.Contains(r.Station))
                {
                    dto.Loaded++;
                    var cargoCode = cargoByStation.TryGetValue(r.Station, out var cc) ? cc : null;
                    dto.LoadedItems.Add(new InventoryDetailItem { Code = r.Code, Station = r.Station, CargoCode = cargoCode });
                }
                else
                {
                    dto.Empty++;
                    dto.EmptyItems.Add(new InventoryDetailItem { Code = r.Code, Station = r.Station });
                }
            }
        }
        return Task.FromResult(dto);
    }

    /// <summary>从库存账本重建当前储位占用集合。</summary>
    public HashSet<string> BuildOccupied()
    {
        var storageMarks = new HashSet<string>(
            _map.GetStations().Where(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0).Select(s => s.Mark),
            StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _invStore.All())
        {
            var st = r.Station ?? "";
            if (string.IsNullOrEmpty(st) || !storageMarks.Contains(st)) continue;
            set.Add(st);
        }
        return set;
    }
}