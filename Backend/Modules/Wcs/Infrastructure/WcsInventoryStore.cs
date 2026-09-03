using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Contracts.Entities;
using GrcsBackend.Modules.Shared.Infrastructure.Repository;

namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// WCS 自持库存账本（wcs_inventory 表）：自动化任务选池/占用/释放的唯一事实源。
/// 生命周期：idle（可选）→ picked（已选未下发）→ busy（在途，task_id 占用任务）→ FINISHED 回 idle 并更新位置。
/// 下发失败记录 task_id = "fail:{id}"（下轮快照对照 GRCS 缓存确认后回收）；
/// 同步重建时 GRCS 锁定记录 task_id = "grcs_lock:{code}"（外部状态，不视为 WCS 在途）。
/// </summary>
public class WcsInventoryStore
{
    public record InvItem(string Code, string Mark, bool IsLoaded, string? CargoCode = null, string? Station = null);

    public const string FailPrefix = "fail:";
    public const string GrcsLockPrefix = "grcs_lock:";
    private const string SyncedMarksKey = "inv_synced_marks";

    private readonly IUnitOfWorkFactory _uow;
    private readonly MapStoreService _map;

    public WcsInventoryStore(IUnitOfWorkFactory uow, MapStoreService map)
    {
        _uow = uow;
        _map = map;
    }

    // ── 查询 ──

    public List<WcsInventoryRow> All()
    {
        using var uow = _uow.Create();
        return uow.Repository<WcsInventoryRow>().Query().OrderBy(r => r.Code).ToList();
    }

    public List<WcsInventoryRow> ByStatus(string status)
    {
        using var uow = _uow.Create();
        return uow.Repository<WcsInventoryRow>().Query().Where(r => r.Status == status).ToList();
    }

    /// <summary>存在 WCS 在途任务（busy 且非 fail:/grcs_lock: 前缀），供同步/地图更新加锁。</summary>
    public bool HasInTransit()
    {
        using var uow = _uow.Create();
        return uow.Repository<WcsInventoryRow>().Query()
            .Any(r => r.Status == "busy" && r.TaskId != "" && !r.TaskId.StartsWith(FailPrefix) && !r.TaskId.StartsWith(GrcsLockPrefix));
    }

    /// <summary>存在下发失败保留的占用（fail 前缀），下轮快照需对照 GRCS 缓存确认回收。</summary>
    public bool HasFailed()
    {
        using var uow = _uow.Create();
        return uow.Repository<WcsInventoryRow>().Query().Any(r => r.TaskId.StartsWith(FailPrefix));
    }

    // ── 选池（自动化每轮调用）──

    /// <summary>
    /// 从账本构建选池：范围过滤（rangeSet=null 全图）→ 仅储位 → 排除非 idle →
    /// 分类（带货托=同站有关联货物；纯货物=无同站托盘的独立货物）。
    /// 返回三池 + 诊断计数（范围内储位托盘总数/其中锁定数/带货托数）。
    /// </summary>
    public (List<InvItem> Empty, List<InvItem> Loaded, List<InvItem> Cargo,
            int Stored, int LockedStored, int LoadedStored) BuildPool(HashSet<string>? rangeSet, HashSet<string> storageMarks)
    {
        var rows = All();
        var empty = new List<InvItem>();
        var loaded = new List<InvItem>();
        var cargo = new List<InvItem>();
        int stored = 0, lockedStored = 0, loadedStored = 0;
        var containerStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cargoStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cargoByStation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (rangeSet != null && !rangeSet.Contains(r.Station)) continue;
            if (!storageMarks.Contains(r.Station)) continue;
            if (r.Code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
            {
                cargoStations.Add(r.Station);
                if (!string.IsNullOrEmpty(r.Station) && !cargoByStation.ContainsKey(r.Station))
                    cargoByStation[r.Station] = r.Code;
            }
            else if (r.Code.Contains("Container", StringComparison.OrdinalIgnoreCase))
            {
                containerStations.Add(r.Station);
                stored++;
                if (r.Status != "idle") lockedStored++;
            }
        }
        foreach (var r in rows)
        {
            if (rangeSet != null && !rangeSet.Contains(r.Station)) continue;
            if (!storageMarks.Contains(r.Station)) continue;
            if (r.Status != "idle") continue;   // busy/picked 均排除
            if (r.Code.Contains("Cargo", StringComparison.OrdinalIgnoreCase))
            {
                if (!containerStations.Contains(r.Station))
                    cargo.Add(new InvItem(r.Code, r.HomeMark, false, null, r.Station));
            }
            else if (r.Code.Contains("Container", StringComparison.OrdinalIgnoreCase))
            {
                var isLoaded = cargoStations.Contains(r.Station);
                var cargoCode = isLoaded && cargoByStation.TryGetValue(r.Station, out var cc) ? cc : null;
                (isLoaded ? loaded : empty).Add(new InvItem(r.Code, r.HomeMark, isLoaded, cargoCode, r.Station));
                if (isLoaded) loadedStored++;
            }
        }
        return (empty, loaded, cargo, stored, lockedStored, loadedStored);
    }

    // ── 占用/释放 ──

    /// <summary>选中容器（已选未下发）：idle → picked。</summary>
    public void Pick(string code)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        var row = repo.FindAsync(code).GetAwaiter().GetResult();
        if (row == null || row.Status != "idle") return;
        row.Status = "picked";
        row.UpdatedAt = DateTime.Now.ToString("O");
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>下发成功：picked → busy，绑定任务。</summary>
    public void MarkBusy(IEnumerable<string> codes, string taskId)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        foreach (var code in codes)
        {
            var row = repo.FindAsync(code).GetAwaiter().GetResult();
            if (row == null || row.Status != "picked") continue;
            row.Status = "busy";
            row.TaskId = taskId;
            row.UpdatedAt = DateTime.Now.ToString("O");
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>下发失败：picked → busy + fail 前缀（下轮快照确认后回收，防「超时但 GRCS 已收」重复选）。</summary>
    public void MarkFail(IEnumerable<string> codes, string taskId)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        foreach (var code in codes)
        {
            var row = repo.FindAsync(code).GetAwaiter().GetResult();
            if (row == null || row.Status != "picked") continue;
            row.Status = "busy";
            row.TaskId = FailPrefix + taskId;
            row.UpdatedAt = DateTime.Now.ToString("O");
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>链 finally 回收：选过但未下发的 picked 记录 → idle。</summary>
    public void RecyclePicked(IEnumerable<string> codes)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        foreach (var code in codes)
        {
            var row = repo.FindAsync(code).GetAwaiter().GetResult();
            if (row == null || row.Status != "picked") continue;
            row.Status = "idle";
            row.TaskId = "";
            row.UpdatedAt = DateTime.Now.ToString("O");
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>任务 FINISHED：busy → idle，位置更新到终点，解除任务绑定。</summary>
    public void Release(string taskId, string destMark)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        foreach (var row in repo.Query().Where(r => r.TaskId == taskId).ToList())
        {
            if (row.Status != "busy") continue;
            row.Status = "idle";
            row.TaskId = "";
            if (!string.IsNullOrEmpty(destMark)) row.Station = destMark;
            row.UpdatedAt = DateTime.Now.ToString("O");
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>下轮快照确认：对照 GRCS 缓存回收失败的占用。容器仍存在（未锁定）→ idle 放回；不存在/锁定 → 保持排除。</summary>
    public void RecycleFailed(IEnumerable<string> stillPresent)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        var present = new HashSet<string>(stillPresent, StringComparer.OrdinalIgnoreCase);
        foreach (var row in repo.Query().Where(r => r.TaskId.StartsWith(FailPrefix)).ToList())
        {
            if (!present.Contains(row.Code)) continue;
            row.Status = "idle";
            row.TaskId = "";
            row.UpdatedAt = DateTime.Now.ToString("O");
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>强制结束：清空全部占用（busy/picked → idle）。</summary>
    public void ClearBusy()
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        foreach (var row in repo.Query().Where(r => r.Status != "idle").ToList())
        {
            row.Status = "idle";
            row.TaskId = "";
            row.UpdatedAt = DateTime.Now.ToString("O");
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    // ── 同步 ──

    /// <summary>全量重建（同步按钮）：清空账本 → 按 GRCS 记录重建；GRCS 锁定记录标 busy（grcs_lock 前缀，不视为 WCS 在途）。</summary>
    public void RebuildFromGrcs(List<CargoInventoryItem> records)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
        foreach (var r in records)
        {
            var code = r.Code ?? "";
            if (string.IsNullOrEmpty(code)) continue;
            repo.AddAsync(new WcsInventoryRow
            {
                Code = code,
                Station = r.CurrentStationCode ?? "",
                HomeMark = r.HomeStationMark ?? "",
                CargoCode = "",
                Status = r.IsLocked ? "busy" : "idle",
                TaskId = r.IsLocked ? GrcsLockPrefix + code : "",
                UpdatedAt = DateTime.Now.ToString("O"),
            }).GetAwaiter().GetResult();
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>增量合并（新增选点点位）：只处理 stationSet 内的记录——新码插入；idle 更新位置；busy/picked 不动。</summary>
    public void SyncMerge(List<CargoInventoryItem> records, HashSet<string> stationSet)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WcsInventoryRow>();
        foreach (var r in records)
        {
            var code = r.Code ?? "";
            if (string.IsNullOrEmpty(code)) continue;
            if (!stationSet.Contains(r.CurrentStationCode ?? "")) continue;
            var row = repo.FindAsync(code).GetAwaiter().GetResult();
            if (row == null)
            {
                repo.AddAsync(new WcsInventoryRow
                {
                    Code = code,
                    Station = r.CurrentStationCode ?? "",
                    HomeMark = r.HomeStationMark ?? "",
                    CargoCode = "",
                    Status = r.IsLocked ? "busy" : "idle",
                    TaskId = r.IsLocked ? GrcsLockPrefix + code : "",
                    UpdatedAt = DateTime.Now.ToString("O"),
                }).GetAwaiter().GetResult();
            }
            else if (row.Status == "idle")
            {
                row.Station = r.CurrentStationCode ?? "";
                row.HomeMark = r.HomeStationMark ?? "";
                row.UpdatedAt = DateTime.Now.ToString("O");
            }
        }
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>上次已同步过的选点范围（kv 持久化），启动轮询时做 diff。</summary>
    public HashSet<string> GetSyncedMarks()
    {
        var s = KvAccess.Get(_uow, SyncedMarksKey);
        if (string.IsNullOrEmpty(s)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(s) ?? [];
        }
        catch { return []; }
    }

    public void SetSyncedMarks(HashSet<string> marks)
    {
        KvAccess.Set(_uow, SyncedMarksKey, System.Text.Json.JsonSerializer.Serialize(marks));
    }

    // ── 储位集合（选池共用的过滤条件）──

    public HashSet<string> StorageMarks()
    {
        var stations = _map.GetStations();
        return new HashSet<string>(stations
            .Where(s => (s.StationType & MapStationTypeBits.StorageLocation) != 0)
            .Select(s => s.Mark), StringComparer.OrdinalIgnoreCase);
    }
}