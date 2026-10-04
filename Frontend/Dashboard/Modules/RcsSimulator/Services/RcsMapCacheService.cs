using Contracts.Rcs.Map;

namespace Dashboard.Modules.RcsSimulator.Services;

/// <summary>
/// 浏览器标签页内共享 RCS 地图数据。页面切换复用内存数据，保存成功后同步更新缓存。
/// </summary>
public sealed class RcsMapCacheService
{
    private readonly RcsApiClient _api;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RcsMapEditorDto> _maps = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeKey;

    public RcsMapCacheService(RcsApiClient api) => _api = api;

    public async Task<RcsMapEditorDto?> GetEditorMapAsync(
        string mapCode = "default", bool forceReload = false, CancellationToken cancellationToken = default)
    {
        mapCode = NormalizeMapCode(mapCode);
        var key = CacheKey(_api.BaseUrl, mapCode);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceReload && _maps.TryGetValue(key, out var cached))
            {
                if (cached.IsActive) _activeKey = key;
                return Clone(cached);
            }

            var loaded = await _api.GetEditorMapAsync(mapCode, cancellationToken);
            if (loaded is null)
            {
                _maps.Remove(key);
                if (string.Equals(_activeKey, key, StringComparison.OrdinalIgnoreCase)) _activeKey = null;
                return null;
            }

            Store(key, loaded);
            if (loaded.IsActive) _activeKey = key;
            else if (string.Equals(_activeKey, key, StringComparison.OrdinalIgnoreCase)) _activeKey = null;
            return Clone(loaded);
        }
        finally { _gate.Release(); }
    }

    /// <summary>读取当前运行地图；常规页面进入直接用缓存，手动刷新才重载后端运行时地图。</summary>
    public async Task<RcsMapEditorDto?> GetActiveMapAsync(
        bool forceReload = false, CancellationToken cancellationToken = default)
    {
        var baseUrl = _api.BaseUrl;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceReload && _activeKey is not null
                && _maps.TryGetValue(_activeKey, out var cached)
                && _activeKey.StartsWith(baseUrl + "\n", StringComparison.OrdinalIgnoreCase))
                return Clone(cached);

            var snapshot = forceReload
                ? await _api.ReloadMapAsync(cancellationToken)
                : await _api.GetMapAsync(cancellationToken);
            if (snapshot is null)
            {
                _activeKey = null;
                return null;
            }

            var key = CacheKey(baseUrl, snapshot.MapCode);
            if (!forceReload && _maps.TryGetValue(key, out cached))
            {
                _activeKey = key;
                return Clone(cached);
            }

            var loaded = await _api.GetEditorMapAsync(snapshot.MapCode, cancellationToken);
            if (loaded is null)
            {
                _activeKey = null;
                return null;
            }

            Store(key, loaded);
            _activeKey = key;
            return Clone(loaded);
        }
        finally { _gate.Release(); }
    }

    /// <summary>只在后端确认保存成功后调用，避免未保存的页面编辑污染共享缓存。</summary>
    public void SetSavedMap(RcsMapEditorDto map)
    {
        var key = CacheKey(_api.BaseUrl, NormalizeMapCode(map.MapCode));
        Store(key, map);
        if (map.IsActive) _activeKey = key;
        else if (string.Equals(_activeKey, key, StringComparison.OrdinalIgnoreCase)) _activeKey = null;
    }

    private void Store(string key, RcsMapEditorDto map) => _maps[key] = Clone(map);

    private static string NormalizeMapCode(string? mapCode) =>
        string.IsNullOrWhiteSpace(mapCode) ? "default" : mapCode.Trim();

    private static string CacheKey(string baseUrl, string mapCode) =>
        baseUrl.TrimEnd('/') + "\n" + mapCode;

    private static RcsMapEditorDto Clone(RcsMapEditorDto map) => new()
    {
        MapCode = map.MapCode,
        Name = map.Name,
        SceneName = map.SceneName,
        SourceType = map.SourceType,
        Version = map.Version,
        Status = map.Status,
        CoordinateSystem = map.CoordinateSystem,
        OriginX = map.OriginX,
        OriginY = map.OriginY,
        OriginZ = map.OriginZ,
        IsActive = map.IsActive,
        Description = map.Description,
        Points = map.Points.Select(point => new RcsMapPointDto
        {
            PointCode = point.PointCode,
            PointName = point.PointName,
            PointType = point.PointType,
            Floor = point.Floor,
            X = point.X,
            Y = point.Y,
            Z = point.Z,
            IsEnabled = point.IsEnabled,
            MetadataJson = point.MetadataJson,
        }).ToList(),
        Lines = map.Lines.Select(line => new RcsMapLineDto
        {
            LineCode = line.LineCode,
            FromPointCode = line.FromPointCode,
            ToPointCode = line.ToPointCode,
            Distance = line.Distance,
            Direction = line.Direction,
            MaxSpeed = line.MaxSpeed,
            IsEnabled = line.IsEnabled,
            MetadataJson = line.MetadataJson,
        }).ToList(),
    };
}
