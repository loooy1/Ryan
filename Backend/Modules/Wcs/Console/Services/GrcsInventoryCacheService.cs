using System.Text.Json;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Proxy.Services;

namespace GrcsBackend.Modules.Wcs.Console.Services;

/// <summary>
/// GRCS 库存查询缓存：按需查询 /api/Cargo 全量记录（无后台轮询）。
/// 供「查库存」按钮实时统计与同步/合并账本使用；查询失败保留旧缓存。
/// 自动化选池已改用 WcsInventoryStore 账本，不再依赖本缓存。
/// </summary>
public class GrcsInventoryCacheService
{
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly ILogger<GrcsInventoryCacheService> _logger;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private List<CargoInventoryItem> _records = [];
    private DateTime _snapshotTime = DateTime.MinValue;
    private bool _lastOk;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public GrcsInventoryCacheService(GrcsHttpClient grcs, WcsSettingsService settings, ILogger<GrcsInventoryCacheService> logger)
    {
        _grcs = grcs;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>最新全图库存记录（锁保护拷贝，调用方只读）。</summary>
    public List<CargoInventoryItem> Records
    {
        get { lock (_lock) return _records.ToList(); }
    }

    /// <summary>最近一次成功快照时间（MinValue = 缓存未就绪，GRCS 不可达或尚未轮询过）。</summary>
    public DateTime SnapshotTime
    {
        get { lock (_lock) return _snapshotTime; }
    }

    /// <summary>是否有过至少一次成功快照（可据此区分「真空库存」与「查询失败」）。</summary>
    public bool Ready => SnapshotTime != DateTime.MinValue;

    /// <summary>全量同步入口（同步按钮/启动轮询建账本）：刷新缓存并返回最新记录。</summary>
    public async Task<List<CargoInventoryItem>> SyncAllAsync()
    {
        await RefreshNowAsync();
        return Records;
    }

    /// <summary>强制刷新一次（任务完成后调用，保证下一步选点前缓存最新）。
    /// 正在刷新中则直接返回当前就绪状态；失败返回 false，沿用旧缓存。</summary>
    public async Task<bool> RefreshNowAsync()
    {
        if (!await _refreshGate.WaitAsync(0)) return Ready;
        try { return await RefreshCoreAsync(); }
        catch { return false; }
        finally { _refreshGate.Release(); }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        var s = _settings.Get();
        if (s == null || string.IsNullOrWhiteSpace(s.GrcsBaseUrl)) return false;
        var (ok, _, json) = await _grcs.QueryCargoInventoryAsync(s.GrcsBaseUrl, s.SceneName);
        if (!ok) return false;
        List<CargoInventoryItem> records;
        try
        {
            var inv = JsonSerializer.Deserialize<CargoQueryResult>(json, JsonOpts);
            records = inv?.Data?.Records ?? [];
        }
        catch { return false; }
        lock (_lock)
        {
            _records = records;
            _snapshotTime = DateTime.Now;
        }
        if (!_lastOk) { _lastOk = true; _logger.LogInformation("库存缓存已恢复（GRCS 库存查询成功，共 {Count} 条）", records.Count); }
        return true;
    }
}