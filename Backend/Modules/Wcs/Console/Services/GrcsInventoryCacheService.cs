using System.Text.Json;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Proxy.Services;

namespace GrcsBackend.Modules.Wcs.Console.Services;

/// <summary>
/// GRCS 库存后台轮询缓存：每 2 秒查询 /api/Cargo 全量记录，
/// 供自动化选点（选托盘/选货物/终点占用判断）与库存统计共用同一份最新数据源，
/// 避免每次选点都阻塞式 HTTP 查询 GRCS。
/// 查询失败保留旧缓存（仅状态翻转时记日志，不刷屏）；
/// 任务完成后可调用 RefreshNowAsync 强制刷新一次，保证链式步骤选点前缓存已反映刚搬动的货。
/// </summary>
public class GrcsInventoryCacheService : IHostedService
{
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly ILogger<GrcsInventoryCacheService> _logger;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private List<CargoInventoryItem> _records = [];
    private DateTime _snapshotTime = DateTime.MinValue;
    private bool _lastOk;
    private CancellationTokenSource? _cts;
    private const int IntervalSeconds = 2;

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

    public Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try { await RefreshCoreAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "库存缓存轮询异常"); }
            try { if (!await timer.WaitForNextTickAsync(ct)) break; } catch { break; }
        }
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