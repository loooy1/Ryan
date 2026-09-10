using System.Collections.Concurrent;
using Contracts.Entities;
using Backend.Shared.Infrastructure.Repository;
using WCSBackend.Modules.Wcs.Console.Services;

namespace WCSBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// WCS 单表库存协调器（wcs_slots 一行管理托盘+货物）。
/// 状态机：ready → picked（已选未下发，在储位）→ LOAD_FINISH（载货成功=已取走）→ transit（号码保留作出发记录）
/// → FINISHED → 清残留（出发储位）+ 写终点储位行（ready）。
/// 下发失败 → fail（在储位，保持锁定）；同步（GRCS 初始化）→ 仅取位置+号码，状态一律 ready。
/// 任务→单元映射存内存：进程重启后映射丢失 → LOAD_FINISH/FINISHED 不推进（保持锁定），由「同步」按钮重建。
/// </summary>
public class WcsInventoryStore
{
    public record InvItem(string Code, string Mark, bool IsLoaded, string? CargoCode = null, string? Station = null);
    public record TransitUnit(string TaskId, string? PalletCode, string? CargoCode, string SourceMark)
    {
        public IEnumerable<string> Codes => new[] { PalletCode, CargoCode }
            .Where(code => !string.IsNullOrWhiteSpace(code))!;
    }

    public const string FailPrefix = "fail:";
    public const string SelectionAvailable = "available";
    public const string SelectionTaskStartLocked = "task_start_locked";
    public const string SelectionTaskEndLocked = "task_end_locked";
    public const string SelectionStartUnavailable = "start_unavailable";
    public const string SelectionDestinationUnavailable = "destination_unavailable";
    private const string SyncedMarksKey = "inv_synced_marks";

    private static readonly object WriteLock = new(); // SQLite 不支持并发写入，全局锁序列化所有写操作

    private readonly IUnitOfWorkFactory _uow;
    private readonly MapStoreService _map;
    private readonly ITaskStageService _stages;
    private readonly ConcurrentDictionary<string, List<string>> _units = new(StringComparer.OrdinalIgnoreCase);

    public WcsInventoryStore(IUnitOfWorkFactory uow, MapStoreService map, ITaskStageService stages)
    {
        _uow = uow;
        _map = map;
        _stages = stages;
    }

    private static bool IsCargo(string code) => code.Contains("Cargo", StringComparison.OrdinalIgnoreCase);

    private static WcsSlotRow? FindRow(IRepository<WcsSlotRow> repo, string code)
        => repo.Query().AsEnumerable().FirstOrDefault(s =>
            string.Equals(s.PalletCode, code, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.CargoCode, code, StringComparison.OrdinalIgnoreCase));

    // ── 查询 ──

    public List<WcsSlotRow> Slots()
    {
        using var uow = _uow.Create();
        return uow.Repository<WcsSlotRow>().Query().OrderBy(r => r.Mark).ToList();
    }

    public WcsSlotRow? FindSlot(string mark)
    {
        using var uow = _uow.Create();
        return uow.Repository<WcsSlotRow>().FindAsync(mark).GetAwaiter().GetResult();
    }

    /// <summary>读取已完成装载、尚未完成任务的在途记录；每条记录包含同次搬运的托盘和货物。</summary>
    public List<TransitUnit> GetTransitUnits()
    {
        var finished = _stages.GetAll()
            .Where(x => string.Equals(x.Stage, "FINISHED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Stage, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<TransitUnit>();
        foreach (var row in _stages.GetAll().Where(x => x.Stage.StartsWith("TRANSIT:", StringComparison.OrdinalIgnoreCase)
            && !finished.Contains(x.TaskId)))
        {
            if (!string.IsNullOrWhiteSpace(row.ContainerCode) || !string.IsNullOrWhiteSpace(row.CargoCode))
                result.Add(new TransitUnit(row.TaskId, row.ContainerCode, row.CargoCode, row.StationCode));
        }
        return result;
    }

    /// <summary>存在在途/锁定单元（picked/transit，不含 fail），供同步/地图更新加锁。</summary>
    public bool HasInTransit()
    {
        using var uow = _uow.Create();
        return uow.Repository<WcsSlotRow>().Query()
            .Any(s => s.PalletStatus == "picked" || s.PalletStatus == "transit"
                || s.CargoStatus == "picked" || s.CargoStatus == "transit");
    }

    /// <summary>
    /// 选择储位后立即持久化占用。只有无托盘/货物，或现有托盘/货物均为 transit 的储位可被预留。
    /// SQLite 写入由全局锁串行化，避免同一 WCS 进程内两个步骤选中同一储位。
    /// </summary>
    public bool TryLockDestinationForTask(string taskId, string mark)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(mark)) return false;
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var row = uow.Repository<WcsSlotRow>().FindAsync(mark).GetAwaiter().GetResult();
            if (row == null) return false;
            if (row.SelectionStatus == SelectionTaskEndLocked
                && string.Equals(row.TaskLockId, taskId, StringComparison.OrdinalIgnoreCase)) return true;
            if (row.SelectionStatus is not (SelectionAvailable or SelectionStartUnavailable)
                || !string.IsNullOrEmpty(row.TaskLockId)) return false;

            row.TaskLockId = taskId;
            row.SelectionStatus = SelectionTaskEndLocked;
            row.UpdatedAt = DateTime.Now.ToString("O");
            uow.CommitAsync().GetAwaiter().GetResult();
            return true;
        }
    }

    /// <summary>仅清除属于该任务的储位预留，防止误释放其他任务的占用。</summary>
    public void ClearTaskLock(string taskId, string? mark)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(mark)) return;
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var row = uow.Repository<WcsSlotRow>().FindAsync(mark).GetAwaiter().GetResult();
            if (row == null || !string.Equals(row.TaskLockId, taskId, StringComparison.OrdinalIgnoreCase)) return;
            row.TaskLockId = "";
            RefreshSelectionStatus(row);
            row.UpdatedAt = DateTime.Now.ToString("O");
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 锁定作为任务起点的储位。普通选点要求库存为 ready；链式任务使用前置终点时，
    /// 起点由当前链路内存指定，不再按库存状态或既有任务锁筛选，直接交由当前链路接管。
    /// </summary>
    public bool TryLockStorageForTask(string taskId, string mark, bool isChainedStart = false)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(mark)) return false;
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var row = uow.Repository<WcsSlotRow>().FindAsync(mark).GetAwaiter().GetResult();
            if (row == null) return false;
            if (row.SelectionStatus == SelectionTaskStartLocked
                && string.Equals(row.TaskLockId, taskId, StringComparison.OrdinalIgnoreCase)) return true;
            if (!isChainedStart
                && (!CanUseAsStart(row)
                    || row.SelectionStatus is SelectionTaskStartLocked or SelectionTaskEndLocked
                    || !string.IsNullOrEmpty(row.TaskLockId))) return false;

            row.TaskLockId = taskId;
            row.SelectionStatus = SelectionTaskStartLocked;
            row.UpdatedAt = DateTime.Now.ToString("O");
            uow.CommitAsync().GetAwaiter().GetResult();
            return true;
        }
    }

    /// <summary>仅清除属于该任务的起点储位锁。</summary>
    /// <summary>已被任务锁定或预留的储位集合，供起点筛选排除。</summary>
    public HashSet<string> GetTaskLockedStorageMarks()
    {
        var storageMarks = StorageMarks();
        using var uow = _uow.Create();
        return uow.Repository<WcsSlotRow>().Query().ToList()
            .Where(s => storageMarks.Contains(s.Mark)
                && s.SelectionStatus is SelectionTaskStartLocked or SelectionTaskEndLocked)
            .Select(s => s.Mark)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> GetStartAvailableStorageMarks()
    {
        var storageMarks = StorageMarks();
        using var uow = _uow.Create();
        return uow.Repository<WcsSlotRow>().Query().AsEnumerable()
            .Where(s => storageMarks.Contains(s.Mark) && CanUseAsStart(s)
                && s.SelectionStatus is not (SelectionTaskStartLocked or SelectionTaskEndLocked))
            .Select(s => s.Mark)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── 储位表（地图镜像）──

    /// <summary>全量重建储位表：清空全部储位行 → 按地图储位+接驳位点重新填充（仅「同步至 WCS」时执行）。</summary>
    public void EnsureSlots()
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
            var stations = _map.GetStations();
            foreach (var st in stations)
            {
                if ((st.StationType & (Contracts.Dtos.MapStationTypeBits.StorageLocation | Contracts.Dtos.MapStationTypeBits.TransferPoint)) == 0) continue;
                var siteType = (st.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0 ? "Storage" : "Terminal";
                repo.AddAsync(new WcsSlotRow
                {
                    Mark = st.Mark,
                    SiteType = siteType,
                    SelectionStatus = siteType == "Storage" ? SelectionStartUnavailable : SelectionAvailable,
                    UpdatedAt = DateTime.Now.ToString("O")
                }).GetAwaiter().GetResult();
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    // ── 选池（自动化每轮调用）──

    /// <summary>
    /// 从储位表构建选池：范围过滤 → 仅就绪（ready）单元分类。
    /// 有托有货=带货托（托盘入口，货物跟随不单独选）；有货无托=纯货物；有托无货=空托；非 ready=锁定。
    /// </summary>
    public (List<InvItem> Empty, List<InvItem> Loaded, List<InvItem> Cargo,
            int Stored, int LockedStored, int LoadedStored) BuildPool(HashSet<string>? rangeSet, HashSet<string> storageMarks)
    {
        using var uow = _uow.Create();
        var slots = uow.Repository<WcsSlotRow>().Query().ToList();
        var empty = new List<InvItem>();
        var loaded = new List<InvItem>();
        var cargo = new List<InvItem>();
        int stored = 0, lockedStored = 0, loadedStored = 0;

        foreach (var s in slots)
        {
            if (rangeSet != null && !rangeSet.Contains(s.Mark)) continue;
            if (!storageMarks.Contains(s.Mark)) continue;
            var hasPallet = !string.IsNullOrEmpty(s.PalletCode);
            var hasCargo = !string.IsNullOrEmpty(s.CargoCode);
            if (hasPallet)
            {
                stored++;
                if (s.PalletStatus != "ready") { lockedStored++; continue; }
                var isLoaded = hasCargo && s.CargoStatus == "ready";
                if (isLoaded)
                {
                    loaded.Add(new InvItem(s.PalletCode, s.Mark, true, s.CargoCode, s.Mark));
                    loadedStored++;
                }
                else
                {
                    empty.Add(new InvItem(s.PalletCode, s.Mark, false, null, s.Mark));
                }
            }
            else if (hasCargo && s.CargoStatus == "ready")
            {
                cargo.Add(new InvItem(s.CargoCode, s.Mark, false, null, s.Mark));
            }
        }
        return (empty, loaded, cargo, stored, lockedStored, loadedStored);
    }

    // ── 状态推进 ──

    /// <summary>选中容器（已选未下发）：ready → picked（还在储位，仅锁定）。</summary>
    public void Pick(string code)
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            var row = FindRow(repo, code);
            if (row == null) return;
            if (IsCargo(code))
            {
                if (row.CargoStatus != "ready") return;
                row.CargoStatus = "picked";
            }
            else
            {
                if (row.PalletStatus != "ready") return;
                row.PalletStatus = "picked";
            }
            RefreshSelectionStatus(row);
            row.UpdatedAt = DateTime.Now.ToString("O");
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 下发成功：登记任务→单元映射（LOAD_FINISH/FINISHED 推进依据）；单元保持在储位（picked，未取走）。
    /// palletCode 供货物入库记录所属托盘（管理用途，本次映射已含链上托盘）。
    /// </summary>
    public void MarkBusy(IEnumerable<string> codes, string taskId, string? startMark = null, string? palletCode = null, bool clearSlotBoth = false)
    {
        lock (WriteLock)
        {
            var list = codes.Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return;
            _units[taskId] = list;
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            foreach (var code in list)
            {
                var row = FindRow(repo, code);
                if (row == null) continue;
                if (IsCargo(code))
                {
                    if (row.CargoStatus == "ready") row.CargoStatus = "picked";
                }
                else
                {
                    if (row.PalletStatus == "ready") row.PalletStatus = "picked";
                }
                RefreshSelectionStatus(row);
                row.UpdatedAt = DateTime.Now.ToString("O");
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// LOAD_FINISH（载货成功 = 已被取走）：将单元写入 task_records/transit，
    /// 清空起点储位并释放起点锁。无任务映射（重启后）不改现场库存。
    /// </summary>
    public void OnTaskLoadFinished(string taskId)
    {
        lock (WriteLock)
        {
            if (!_units.TryGetValue(taskId, out var codes)) return;
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            string? palletCode = null;
            string? cargoCode = null;
            string? sourceMark = null;
            foreach (var code in codes)
            {
                var row = FindRow(repo, code);
                if (row == null) continue;
                sourceMark ??= row.Mark;
                if (IsCargo(code)) cargoCode = code;
                else palletCode = code;
                if (IsCargo(code))
                {
                    if (string.Equals(row.CargoCode, code, StringComparison.OrdinalIgnoreCase))
                    {
                        row.CargoCode = "";
                        row.CargoStatus = "";
                    }
                }
                else if (string.Equals(row.PalletCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    row.PalletCode = "";
                    row.PalletStatus = "";
                }
                if (string.Equals(row.TaskLockId, taskId, StringComparison.OrdinalIgnoreCase))
                    row.TaskLockId = "";
                RefreshSelectionStatus(row);
                row.UpdatedAt = DateTime.Now.ToString("O");
            }
            uow.CommitAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(palletCode) || !string.IsNullOrWhiteSpace(cargoCode))
                _stages.TryRecordSystemEvent(taskId, $"TRANSIT:{palletCode}({cargoCode})", true, 0,
                    sourceMark, palletCode, cargoCode);
        }
    }

    /// <summary>下发失败：picked/ready → fail（号码保留，锁定中；强制结束或同步重建解除）。</summary>
    public void MarkFail(IEnumerable<string> codes, string taskId)
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            foreach (var code in codes)
            {
                if (string.IsNullOrEmpty(code)) continue;
                var row = FindRow(repo, code);
                if (row == null) continue;
                if (IsCargo(code))
                {
                    if (row.CargoStatus != "ready" && row.CargoStatus != "picked") continue;
                    row.CargoStatus = "fail";
                }
                else
                {
                    if (row.PalletStatus != "ready" && row.PalletStatus != "picked") continue;
                    row.PalletStatus = "fail";
                }
                RefreshSelectionStatus(row);
                row.UpdatedAt = DateTime.Now.ToString("O");
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// FINISHED 落地：按任务映射清残留（出发储位记录）→ 写终点储位行（号码+ready）。
    /// 终点非储位（接驳位/出库）= 只清残留（单元离开储位）；无映射（进程重启后）→ 不落地，保持锁定（由同步重建）。
    /// </summary>
    public void Release(string taskId, string destMark)
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            _units.TryRemove(taskId, out var mappedCodes);
            var codes = mappedCodes is { Count: > 0 } ? mappedCodes
                : GetTransitUnits().Where(x => string.Equals(x.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(x => x.Codes).ToList();
            if (codes.Count == 0) return;
            foreach (var code in codes)
            {
                var rows = repo.Query().AsEnumerable().Where(s =>
                    string.Equals(s.PalletCode, code, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.CargoCode, code, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var row in rows)
                {
                    if (string.Equals(row.PalletCode, code, StringComparison.OrdinalIgnoreCase)) { row.PalletCode = ""; row.PalletStatus = ""; }
                    if (string.Equals(row.CargoCode, code, StringComparison.OrdinalIgnoreCase)) { row.CargoCode = ""; row.CargoStatus = ""; }
                    RefreshSelectionStatus(row);
                    row.UpdatedAt = DateTime.Now.ToString("O");
                }
            }
            if (!string.IsNullOrEmpty(destMark))
            {
                var dest = repo.FindAsync(destMark).GetAwaiter().GetResult();
                if (dest != null)
                {
                    foreach (var code in codes)
                    {
                        if (IsCargo(code)) { dest.CargoCode = code; dest.CargoStatus = "ready"; }
                        else { dest.PalletCode = code; dest.PalletStatus = "ready"; }
                    }
                    if (string.Equals(dest.TaskLockId, taskId, StringComparison.OrdinalIgnoreCase))
                        dest.TaskLockId = "";
                    RefreshSelectionStatus(dest);
                    dest.UpdatedAt = DateTime.Now.ToString("O");
                }
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>链 finally 回收：选过但未下发的 picked 记录 → ready。</summary>
    public void RecyclePicked(IEnumerable<string> codes)
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            foreach (var code in codes)
            {
                if (string.IsNullOrEmpty(code)) continue;
                var row = FindRow(repo, code);
                if (row == null) continue;
                if (IsCargo(code))
                {
                    if (row.CargoStatus != "picked") continue;
                    row.CargoStatus = "ready";
                }
                else
                {
                    if (row.PalletStatus != "picked") continue;
                    row.PalletStatus = "ready";
                }
                RefreshSelectionStatus(row);
                row.UpdatedAt = DateTime.Now.ToString("O");
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>强制结束：全部非 ready → ready（号码保留，位置可能与实际不符，同步兜底）。</summary>
    public void ClearBusy()
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            foreach (var s in repo.Query().ToList())
            {
                if (s.PalletStatus != "" && s.PalletStatus != "ready") s.PalletStatus = "ready";
                if (s.CargoStatus != "" && s.CargoStatus != "ready") s.CargoStatus = "ready";
                RefreshSelectionStatus(s);
                s.UpdatedAt = DateTime.Now.ToString("O");
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    // ── 同步 ──

    /// <summary>全量重建（同步按钮）：清空储位行占用 → 按 GRCS 记录回填（仅储位点；接驳位等非储位记录跳过，任务链会恢复）。
    /// GRCS 仅提供初始化（位置+号码），状态一律 ready（不照搬 GRCS 锁定）；同步后旧任务映射作废（后续 FINISHED 不落地）。</summary>
    public void RebuildFromGrcs(List<Contracts.Dtos.CargoInventoryItem> records)
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            foreach (var slot in repo.Query().ToList())
            {
                slot.PalletCode = ""; slot.PalletStatus = "";
                slot.CargoCode = ""; slot.CargoStatus = "";
                slot.TaskLockId = "";
                RefreshSelectionStatus(slot);
                slot.UpdatedAt = DateTime.Now.ToString("O");
            }
            foreach (var r in records)
            {
                var code = r.Code ?? "";
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(r.CurrentStationCode)) continue;
                var slot = repo.FindAsync(r.CurrentStationCode).GetAwaiter().GetResult();
                if (slot == null) continue;
                if (IsCargo(code)) { slot.CargoCode = code; slot.CargoStatus = "ready"; }
                else { slot.PalletCode = code; slot.PalletStatus = "ready"; }
                RefreshSelectionStatus(slot);
                slot.UpdatedAt = DateTime.Now.ToString("O");
            }
            _units.Clear();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>增量合并（新增选点点位）：只处理 stationSet 内的储位记录——新码插入空位；占用中跳过。状态一律 ready。</summary>
    public void SyncMerge(List<Contracts.Dtos.CargoInventoryItem> records, HashSet<string>? stationSet)
    {
        lock (WriteLock)
        {
            using var uow = _uow.Create();
            var repo = uow.Repository<WcsSlotRow>();
            foreach (var r in records)
            {
                var code = r.Code ?? "";
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(r.CurrentStationCode)) continue;
                if (stationSet != null && !stationSet.Contains(r.CurrentStationCode)) continue;
                var slot = repo.FindAsync(r.CurrentStationCode).GetAwaiter().GetResult();
                if (slot == null) continue;
                if (IsCargo(code))
                {
                    if (!string.IsNullOrEmpty(slot.CargoCode)) continue;
                    slot.CargoCode = code; slot.CargoStatus = "ready";
                }
                else
                {
                    if (!string.IsNullOrEmpty(slot.PalletCode)) continue;
                    slot.PalletCode = code; slot.PalletStatus = "ready";
                }
                RefreshSelectionStatus(slot);
                slot.UpdatedAt = DateTime.Now.ToString("O");
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>上次已同步过的选点范围（kv 持久化），启动轮询时做 diff。</summary>
    public HashSet<string> GetSyncedMarks()
    {
        lock (WriteLock)
        {
            var s = KvAccess.Get(_uow, SyncedMarksKey);
            if (string.IsNullOrEmpty(s)) return [];
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(s) ?? [];
            }
            catch { return []; }
        }
    }

    public void SetSyncedMarks(HashSet<string> marks)
    {
        lock (WriteLock)
        {
            KvAccess.Set(_uow, SyncedMarksKey, System.Text.Json.JsonSerializer.Serialize(marks));
        }
    }

    // ── 储位集合（选池共用的过滤条件）──

    public HashSet<string> StorageMarks()
    {
        var stations = _map.GetStations();
        return new HashSet<string>(stations
            .Where(s => (s.StationType & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0)
            .Select(s => s.Mark), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>储位+接驳位（任务终点可落地容器的非储位点）集合：查库存展示与储位表建行共用。</summary>
    public HashSet<string> StorageAndTerminalMarks()
    {
        var stations = _map.GetStations();
        return new HashSet<string>(stations
            .Where(s => (s.StationType & (Contracts.Dtos.MapStationTypeBits.StorageLocation | Contracts.Dtos.MapStationTypeBits.TransferPoint)) != 0)
            .Select(s => s.Mark), StringComparer.OrdinalIgnoreCase);
    }

    private static bool CanUseAsDestination(WcsSlotRow slot)
        => (string.IsNullOrEmpty(slot.PalletCode) || string.Equals(slot.PalletStatus, "transit", StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(slot.CargoCode) || string.Equals(slot.CargoStatus, "transit", StringComparison.OrdinalIgnoreCase));

    private static void RefreshSelectionStatus(WcsSlotRow slot)
    {
        if (!string.IsNullOrEmpty(slot.TaskLockId))
        {
            if (slot.SelectionStatus != SelectionTaskEndLocked)
                slot.SelectionStatus = SelectionTaskStartLocked;
            return;
        }

        if (!string.Equals(slot.SiteType, "Storage", StringComparison.OrdinalIgnoreCase))
        {
            slot.SelectionStatus = SelectionAvailable;
            return;
        }

        slot.SelectionStatus = CanUseAsDestination(slot)
            ? (CanUseAsStart(slot) ? SelectionDestinationUnavailable : SelectionStartUnavailable)
            : SelectionDestinationUnavailable;
    }

    private static bool CanUseAsStart(WcsSlotRow slot)
        => (!string.IsNullOrEmpty(slot.PalletCode) && string.Equals(slot.PalletStatus, "ready", StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(slot.CargoCode) && string.Equals(slot.CargoStatus, "ready", StringComparison.OrdinalIgnoreCase));
}
