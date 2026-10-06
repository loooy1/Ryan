using Contracts.Rcs.Map;
using Contracts.Rcs.Vehicle;

namespace Dashboard.Modules.RcsSimulator.Services;

/// <summary>车辆管理页跨路由共享的内存缓存，并将 RCS 实时推送合并进车辆状态。</summary>
public sealed class RcsVehicleCacheService : IDisposable
{
    private readonly RcsApiClient _api;
    private readonly RcsRealtimeHubClient _realtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<RcsVehicleCacheSnapshot>? Changed;

    public RcsVehicleCacheService(RcsApiClient api, RcsRealtimeHubClient realtime)
    {
        _api = api;
        _realtime = realtime;
        _realtime.VehicleStateChanged += OnVehicleStateChanged;
        _realtime.VehiclesChanged += OnVehiclesChanged;
        _realtime.Reconnected += OnReconnected;
    }

    public RcsVehicleCacheSnapshot? Current => TryGetEntry(_api.BaseUrl, out var entry) && entry.Loaded
        ? Snapshot(entry) : null;

    public string SelectedVehicleId
    {
        get => TryGetEntry(_api.BaseUrl, out var entry) ? entry.SelectedVehicleId : "";
        set => GetOrCreate(_api.BaseUrl).SelectedVehicleId = value ?? "";
    }

    public async Task<RcsVehicleCacheSnapshot> EnsureLoadedAsync(CancellationToken token = default)
    {
        if (Current is { } cached)
        {
            await EnsureRealtimeAsync();
            return cached;
        }
        return await RefreshAsync(token);
    }

    public async Task<RcsVehicleCacheSnapshot> RefreshAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var baseUrl = _api.BaseUrl;
            var entry = GetOrCreate(baseUrl);
            var vehicles = await _api.GetVehiclesAsync(token);
            var protocols = await _api.GetVehicleProtocolsAsync(token);
            RcsMapSnapshot? map;
            try { map = await _api.GetMapAsync(token); }
            catch { map = entry.Map; }

            entry.Vehicles.Clear();
            foreach (var vehicle in vehicles) entry.Vehicles[vehicle.Id] = vehicle;
            entry.Protocols = protocols;
            entry.Map = map;
            entry.Loaded = true;
            if (!entry.Vehicles.ContainsKey(entry.SelectedVehicleId))
                entry.SelectedVehicleId = entry.Vehicles.Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault() ?? "";
            var snapshot = Snapshot(entry);
            Changed?.Invoke(snapshot);
            await EnsureRealtimeAsync();
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    public void ReplaceVehicles(IEnumerable<VehicleStateDto> vehicles)
    {
        var entry = GetOrCreate(_api.BaseUrl);
        entry.Vehicles.Clear();
        foreach (var vehicle in vehicles) entry.Vehicles[vehicle.Id] = vehicle;
        if (!entry.Vehicles.ContainsKey(entry.SelectedVehicleId))
            entry.SelectedVehicleId = entry.Vehicles.Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault() ?? "";
        Changed?.Invoke(Snapshot(entry));
    }

    public void UpsertVehicle(VehicleStateDto vehicle)
    {
        var entry = GetOrCreate(_api.BaseUrl);
        entry.Vehicles[vehicle.Id] = vehicle;
        Changed?.Invoke(Snapshot(entry));
    }

    public void RemoveVehicle(string id)
    {
        var entry = GetOrCreate(_api.BaseUrl);
        entry.Vehicles.Remove(id);
        if (string.Equals(entry.SelectedVehicleId, id, StringComparison.OrdinalIgnoreCase))
            entry.SelectedVehicleId = entry.Vehicles.Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault() ?? "";
        Changed?.Invoke(Snapshot(entry));
    }

    private async Task EnsureRealtimeAsync()
    {
        _realtime.SetBaseUrl(_api.BaseUrl);
        if (_realtime.IsConnected) return;
        try { await _realtime.StartAsync(); } catch { }
    }

    private void OnVehicleStateChanged(VehicleStateDto vehicle) => UpsertVehicle(vehicle);

    private void OnVehiclesChanged(IReadOnlyList<VehicleStateDto> vehicles) => ReplaceVehicles(vehicles);

    private void OnReconnected() => _ = RefreshAfterReconnectAsync();

    private async Task RefreshAfterReconnectAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception) when (_disposed) { }
        catch { /* Reconnect refresh is best-effort; the page's manual refresh remains available. */ }
    }

    private bool TryGetEntry(string baseUrl, out CacheEntry entry) => _entries.TryGetValue(Key(baseUrl), out entry!);

    private CacheEntry GetOrCreate(string baseUrl)
    {
        var key = Key(baseUrl);
        if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new CacheEntry();
        return entry;
    }

    private static string Key(string baseUrl) => baseUrl.TrimEnd('/');

    private static RcsVehicleCacheSnapshot Snapshot(CacheEntry entry) => new(
        entry.Vehicles.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(),
        entry.Protocols, entry.Map, entry.SelectedVehicleId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _realtime.VehicleStateChanged -= OnVehicleStateChanged;
        _realtime.VehiclesChanged -= OnVehiclesChanged;
        _realtime.Reconnected -= OnReconnected;
        _gate.Dispose();
    }

    private sealed class CacheEntry
    {
        public Dictionary<string, VehicleStateDto> Vehicles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<VehicleProtocolInfoDto> Protocols { get; set; } = [];
        public RcsMapSnapshot? Map { get; set; }
        public string SelectedVehicleId { get; set; } = "";
        public bool Loaded { get; set; }
    }
}

public sealed record RcsVehicleCacheSnapshot(
    IReadOnlyList<VehicleStateDto> Vehicles,
    IReadOnlyList<VehicleProtocolInfoDto> Protocols,
    RcsMapSnapshot? Map,
    string SelectedVehicleId);
