using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;

/// <summary>kv 表读写助手（UoW 基座，统一 Key/Value 存取）。</summary>
internal static class KvAccess
{
    public static string? Get(IUnitOfWorkFactory uowFactory, string key)
    {
        using var uow = uowFactory.Create();
        return uow.Repository<KvRow>().FindAsync(key).GetAwaiter().GetResult()?.Value;
    }

    public static void Set(IUnitOfWorkFactory uowFactory, string key, string value)
    {
        using var uow = uowFactory.Create();
        var repo = uow.Repository<KvRow>();
        var row = repo.FindAsync(key).GetAwaiter().GetResult();
        if (row == null) repo.AddAsync(new KvRow { Key = key, Value = value }).GetAwaiter().GetResult();
        else row.Value = value;
        uow.CommitAsync().GetAwaiter().GetResult();
    }
}

/// <summary>站点池缓存（地图上传/GRCS 拉取后持久化，重启不丢）。Singleton。</summary>
public class MapStoreService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<MapStationLite> _stations = [];
    private string _savedAt = "";
    private int _pathsCount;

    public MapStoreService(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "map_stations");
            if (!string.IsNullOrEmpty(json))
            {
                var dto = new KvRow { Value = json }.Adapt<MapUploadDto>();
                _stations = dto.Stations;
                _savedAt = dto.SavedAt;
                _pathsCount = dto.PathsCount;
            }
        }
        catch { }
    }

    public void Save(MapUploadDto dto)
    {
        lock (_lock)
        {
            _stations = dto.Stations ?? [];
            _savedAt = dto.SavedAt;
            _pathsCount = dto.PathsCount;
            KvAccess.Set(_uow, "map_stations", dto.Adapt<KvRow>().Value);
        }
    }

    public List<MapStationLite> GetStations() { lock (_lock) { return _stations.ToList(); } }

    public object Snapshot() { lock (_lock) { return new { savedAt = _savedAt, pathsCount = _pathsCount, stations = _stations }; } }
}

/// <summary>选点范围配置（内存 + SQLite 持久化）。Singleton。</summary>
public class RangeConfigService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private RangeConfigDto _range = new();

    public RangeConfigService(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "auto_range");
            if (!string.IsNullOrEmpty(json))
                _range = new KvRow { Value = json }.Adapt<RangeConfigDto>();
        }
        catch { }
    }

    public RangeConfigDto Get()
    {
        lock (_lock)
        {
            var copy = JsonSerializer.Deserialize<RangeConfigDto>(JsonSerializer.Serialize(_range))!;
            copy.TypeFilter = 0;   // 站点类型过滤已废弃
            return copy;
        }
    }

    public void Set(RangeConfigDto range)
    {
        lock (_lock)
        {
            // 站点类型过滤已废弃：写入时强制清零（清理历史遗留值）
            range.TypeFilter = 0;
            _range = range;
            KvAccess.Set(_uow, "auto_range", _range.Adapt<KvRow>().Value);
        }
    }
}

/// <summary>运行配置（GRCS 地址/场景名；前端「连接设置」PUT 到这里）。Singleton。</summary>
public class WcsSettingsService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private WcsSettingsDto _settings = new();

    public WcsSettingsService(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "wcs_settings");
            if (!string.IsNullOrEmpty(json))
                _settings = new KvRow { Value = json }.Adapt<WcsSettingsDto>();
        }
        catch { }
    }

    public WcsSettingsDto Get() { lock (_lock) { return new WcsSettingsDto { GrcsBaseUrl = _settings.GrcsBaseUrl, SceneName = _settings.SceneName }; } }

    public void Set(WcsSettingsDto s)
    {
        lock (_lock)
        {
            _settings.GrcsBaseUrl = string.IsNullOrWhiteSpace(s.GrcsBaseUrl) ? _settings.GrcsBaseUrl : s.GrcsBaseUrl.Trim();
            _settings.SceneName = s.SceneName ?? "";
            KvAccess.Set(_uow, "wcs_settings", _settings.Adapt<KvRow>().Value);
        }
    }
}

/// <summary>归巢模式配置（地图框选巢区站点 Mark 列表，内存 + SQLite 持久化，与 auto_range 相互独立）。Singleton。</summary>
public class NestConfigService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private NestConfigDto _config = new();

    public NestConfigService(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "nest_config");
            if (!string.IsNullOrEmpty(json))
                _config = new KvRow { Value = json }.Adapt<NestConfigDto>();
        }
        catch { }
    }

    public NestConfigDto Get()
    {
        lock (_lock) { return new NestConfigDto { Marks = _config.Marks.ToList() }; }
    }

    public void Set(NestConfigDto config)
    {
        lock (_lock)
        {
            _config.Marks = (config.Marks ?? [])
                .Select(m => m?.Trim() ?? "")
                .Where(m => m.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            KvAccess.Set(_uow, "nest_config", _config.Adapt<KvRow>().Value);
        }
    }
}

/// <summary>段1 任务 → 货物码映射（入库段2 与信号放行共用同一码）。Singleton。</summary>
public class CargoCodeStore
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private Dictionary<string, string> _map = [];

    public CargoCodeStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "cargo_codes");
            if (!string.IsNullOrEmpty(json))
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Opts) ?? [];
        }
        catch { }
    }

    public string Ensure(string seg1TaskId)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(seg1TaskId, out var existing)) return existing;
            var code = "SimCargo_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x").ToUpper();
            _map[seg1TaskId] = code;
            KvAccess.Set(_uow, "cargo_codes", JsonSerializer.Serialize(_map));
            return code;
        }
    }
}

/// <summary>站点锁（纯内存 Singleton）：流程终点任务 FINISHED 后由 TaskStageService 事件即时释放。</summary>
public class StationLockStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StationLockEntry> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前仍被锁定的站点（顺带按已完成任务惰性清理）。</summary>
    public HashSet<string> GetLocked(ITaskStageService stages)
    {
        lock (_lock)
        {
            if (_locks.Count == 0) return [];
            var finished = stages.FinishedTaskIds;
            if (finished.Count > 0)
            {
                foreach (var st in _locks.Where(kv => finished.Contains(kv.Value.TaskId)).Select(kv => kv.Key).ToList())
                    _locks.Remove(st);
            }
            return _locks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Acquire(string station, string flowEndTaskId)
    {
        if (string.IsNullOrEmpty(station)) return;
        lock (_lock)
        {
            if (_locks.ContainsKey(station)) return;
            _locks[station] = new StationLockEntry { TaskId = flowEndTaskId };
        }
    }

    /// <summary>释放指定站点锁（任务 FINISHED 后由自动化引擎调用）。</summary>
    public void Release(string station)
    {
        if (string.IsNullOrEmpty(station)) return;
        lock (_lock) _locks.Remove(station);
    }
}

/// <summary>任务台账壳（底层为 task_records 合并表创建行，上限 10000 条）。Singleton。</summary>
public class LedgerStore
{
    private readonly ITaskStageService _stages;

    public LedgerStore(ITaskStageService stages) => _stages = stages;

    /// <summary>写创建行（同一任务只写一条，重复调用后端跳过）并 SignalR 广播。</summary>
    public Task AppendAsync(List<TaskLedgerEntry> entries)
    {
        _stages.RecordCreated(entries);
        return Task.CompletedTask;
    }

    /// <summary>读创建行（投影为台账条目，id 倒序）。</summary>
    public List<TaskLedgerEntry> Get(int limit = 500) => _stages.GetCreated(limit);

    /// <summary>清空全表（创建行 + 阶段行）并广播 EventsReset。</summary>
    public void Clear() => _stages.ClearAll();
}

/// <summary>
/// 信号确认状态（SQLite workflow_state 表，kind = arrival / removal / sent）。
/// Set 是幂等抢占：新插入返回 true（claimed），已存在返回 false——前端据此
/// 在发信号前抢占，防止多标签页对同一任务重复发送 WCS 信号。
/// </summary>
public class SignalConfirmStore
{
    private readonly IUnitOfWorkFactory _uow;

    public SignalConfirmStore(IUnitOfWorkFactory uowFactory) => _uow = uowFactory;

    public bool Set(string kind, string taskId, string? value)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<WorkflowStateRow>();
        var existing = repo.Query().FirstOrDefault(x => x.Kind == kind && x.TaskId == taskId);
        if (existing != null) return false;
        repo.AddAsync(new WorkflowStateRow { Kind = kind, TaskId = taskId, Value = value, Time = DateTime.Now.ToString("O") })
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        return true;
    }

    public void Remove(string kind, string taskId)
    {
        using var uow = _uow.Create();
        uow.Repository<WorkflowStateRow>().DeleteWhereAsync(x => x.Kind == kind && x.TaskId == taskId)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>全部确认状态按 kind 分组返回。</summary>
    public Dictionary<string, List<WorkflowStateRow>> GetAll()
    {
        using var uow = _uow.Create();
        return uow.Repository<WorkflowStateRow>().FindAllAsync().GetAwaiter().GetResult()
            .GroupBy(r => r.Kind)
            .ToDictionary(g => g.Key, g => g.ToList());
    }
}

/// <summary>异常记录台账（SQLite exception_records 表，纯 HTTP 读写）。Singleton。</summary>
public class ExceptionRecordStore
{
    private readonly IUnitOfWorkFactory _uow;

    public ExceptionRecordStore(IUnitOfWorkFactory uowFactory) => _uow = uowFactory;

    public long Add(Entities.ExceptionRecordDto rec)
    {
        rec.CreatedAt = DateTime.Now.ToString("O");
        rec.UpdatedAt = DateTime.Now.ToString("O");
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ExceptionRecordDto>();
        repo.AddAsync(rec).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        return rec.Id;
    }

    public List<Entities.ExceptionRecordDto> GetAll(string? vehicle = null, string? dateFrom = null, string? dateTo = null, string? status = null, string? dept = null, string? project = null)
    {
        using var uow = _uow.Create();
        var q = uow.Repository<Entities.ExceptionRecordDto>().Query();
        if (!string.IsNullOrEmpty(vehicle)) q = q.Where(r => r.VehicleCode == vehicle);
        if (!string.IsNullOrEmpty(dateFrom)) q = q.Where(r => string.Compare(r.HappenedAt, dateFrom) >= 0);
        if (!string.IsNullOrEmpty(dateTo)) q = q.Where(r => string.Compare(r.HappenedAt, dateTo) <= 0);
        if (!string.IsNullOrEmpty(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = q.Where(r => statuses.Contains(r.Status));
        }
        if (!string.IsNullOrEmpty(dept)) q = q.Where(r => r.ResponsibleDept == dept);
        if (!string.IsNullOrEmpty(project)) q = q.Where(r => r.Project == project);
        return q.OrderByDescending(r => r.Id).ToList();
    }

    /// <summary>项目名去重列表（含空串=未分类）。</summary>
    public List<string> Projects()
    {
        using var uow = _uow.Create();
        return uow.Repository<Entities.ExceptionRecordDto>().Query()
            .Select(r => r.Project).Distinct().ToList();
    }

    /// <summary>删除某项目下的全部记录。</summary>
    public void RemoveByProject(string project)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ExceptionRecordDto>().DeleteWhereAsync(r => r.Project == project)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Update(Entities.ExceptionRecordDto rec)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ExceptionRecordDto>();
        var row = repo.FindAsync(rec.Id).GetAwaiter().GetResult();
        if (row == null) return;
        row.HappenedAt = rec.HappenedAt;
        row.VehicleCode = rec.VehicleCode;
        row.Phenomenon = rec.Phenomenon;
        row.Reason = rec.Reason;
        row.Progress = rec.Progress;
        row.ResponsibleDept = rec.ResponsibleDept;
        row.Status = rec.Status;
        row.Project = rec.Project;
        row.ReproducedAt = rec.ReproducedAt;
        row.ReproduceCount = rec.ReproduceCount;
        row.UpdatedAt = DateTime.Now.ToString("O");
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>复现三联动：次数 +1、复现时间=当前、置为未解决；车号追加（顿号分隔，去重）。</summary>
    public void Reproduce(long id, string? vehicleCode)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ExceptionRecordDto>();
        var row = repo.FindAsync(id).GetAwaiter().GetResult();
        if (row == null) return;
        if (!string.IsNullOrEmpty(vehicleCode))
        {
            var existing = row.VehicleCode ?? "";
            if (existing.Length == 0) row.VehicleCode = vehicleCode;
            else if (!existing.Contains(vehicleCode, StringComparison.Ordinal))
                row.VehicleCode = existing + "、" + vehicleCode;
        }
        else row.VehicleCode = "";
        row.ReproduceCount += 1;
        row.ReproducedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        row.Status = "pending";
        row.UpdatedAt = DateTime.Now.ToString("O");
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Remove(long id)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ExceptionRecordDto>().DeleteWhereAsync(r => r.Id == id)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }
}

/// <summary>项目记录（SQLite project_logs 表，纯 HTTP 读写）。Singleton。</summary>
public class ProjectLogStore
{
    private readonly IUnitOfWorkFactory _uow;

    public ProjectLogStore(IUnitOfWorkFactory uowFactory) => _uow = uowFactory;

    public long Add(Entities.ProjectLogDto rec)
    {
        rec.CreatedAt = DateTime.Now.ToString("O");
        rec.UpdatedAt = DateTime.Now.ToString("O");
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ProjectLogDto>();
        repo.AddAsync(rec).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        return rec.Id;
    }

    public List<Entities.ProjectLogDto> GetAll(string? dateFrom = null, string? dateTo = null, string? status = null, string? project = null)
    {
        using var uow = _uow.Create();
        var q = uow.Repository<Entities.ProjectLogDto>().Query();
        if (!string.IsNullOrEmpty(dateFrom)) q = q.Where(r => string.Compare(r.LogDate, dateFrom) >= 0);
        if (!string.IsNullOrEmpty(dateTo)) q = q.Where(r => string.Compare(r.LogDate, dateTo) <= 0);
        if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrEmpty(project)) q = q.Where(r => r.Project == project);
        return q.OrderByDescending(r => r.Id).ToList();
    }

    /// <summary>项目名去重列表（含空串=未分类）。</summary>
    public List<string> Projects()
    {
        using var uow = _uow.Create();
        return uow.Repository<Entities.ProjectLogDto>().Query()
            .Select(r => r.Project).Distinct().ToList();
    }

    /// <summary>删除某项目下的全部记录。</summary>
    public void RemoveByProject(string project)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ProjectLogDto>().DeleteWhereAsync(r => r.Project == project)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Update(Entities.ProjectLogDto rec)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ProjectLogDto>();
        var row = repo.FindAsync(rec.Id).GetAwaiter().GetResult();
        if (row == null) return;
        row.LogDate = rec.LogDate;
        row.Content = rec.Content;
        row.Status = rec.Status;
        row.Project = rec.Project;
        row.Remark = rec.Remark;
        row.UpdatedAt = DateTime.Now.ToString("O");
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Remove(long id)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ProjectLogDto>().DeleteWhereAsync(r => r.Id == id)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }
}