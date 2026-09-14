using WCSBackend.Modules.Wcs.Infrastructure;
using InvItem = WCSBackend.Modules.Wcs.Infrastructure.WcsInventoryStore.InvItem;
using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>WCS 双表库存协调器：选池构建 / 库存统计 / 储位占用计算（容器表 + 储位表）。</summary>
public class InventoryCoordinator
{
    private readonly WcsInventoryStore _invStore;
    private readonly RangeConfigService _range;
    private readonly AutomationLogService _log;

    public InventoryCoordinator(WcsInventoryStore invStore,
        RangeConfigService range, AutomationLogService log)
    {
        _invStore = invStore; _range = range;
        _log = log;
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

    /// <summary>库存统计（储位+接驳位表维度）：锁定中 = 非 ready 且非 transit 状态单元（picked 在储位/fail）；
    /// 在途 = transit（已取走，号码保留作出发记录）单独区块；
    /// 同一储位托盘+货物均在锁定/在途 → 合并一行（Code=托盘号, CargoCode=货物号）；
    /// 空托/带货托/纯货物 = ready 储位行分类（有托有货=带货托；有货无托=纯货物；有托无货=空托）；
    /// 接驳位 = 非储位终点行上的容器（transit 归在途区块，其余归接驳位区块），绕过储位范围过滤。</summary>
    /// <summary>从储位表重建终点选点占用集合：任务预留，或号码存在且非 transit 的储位，均不可选。</summary>
    public HashSet<string> BuildOccupied()
    {
        var storageMarks = _invStore.StorageMarks();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _invStore.Slots())
        {
            if (!storageMarks.Contains(s.Mark)) continue;
            if (s.SelectionStatus is WcsInventoryStore.SelectionDestinationUnavailable
                or WcsInventoryStore.SelectionTaskStartLocked
                or WcsInventoryStore.SelectionTaskEndLocked)
                set.Add(s.Mark);
        }
        return set;
    }
}
