using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;
using Rcs.VirtualVehicle;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Application.Vehicles;

/// <summary>车辆实例和配置缓存。只有协议通道接触车辆对象；配置变更由车辆管理服务协调。</summary>
public sealed class RcsVehicleRegistry(IRcsVehicleStore store, RcsMapCache maps,
    ILogger<RcsVehicleRegistry> logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly Dictionary<string, Entry> _vehicles = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    public event Action<VehicleStateDto>? StateChanged;
    public event Action<IReadOnlyList<VehicleStateDto>>? VehiclesChanged;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _initialization.WaitAsync(token);
        try
        {
            if (_initialized) return;
            var definitions = await store.ListAsync(token);
            foreach (var row in definitions) Add(row, publish: false);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    public IReadOnlyList<VehicleStateDto> GetStates()
    {
        lock (_gate) return _vehicles.Values.Select(x => Decorate(x.Vehicle.State, x.Definition))
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
    public IVirtualVehicle GetVehicle(string id)
    {
        lock (_gate) return _vehicles.TryGetValue(id, out var entry) ? entry.Vehicle
            : throw new ArgumentException($"车辆 {id} 不存在。");
    }
    public RcsVehicleRow GetDefinition(string id)
    {
        lock (_gate) return _vehicles.TryGetValue(id, out var entry) ? Copy(entry.Definition)
            : throw new ArgumentException($"车辆 {id} 不存在。");
    }
    public bool Contains(string id) { lock (_gate) return _vehicles.ContainsKey(id); }

    public void Add(RcsVehicleRow row, bool publish = true)
    {
        var vehicle = new VirtualVehicleSimulator(row.VehicleId);
        if (row.InitialPointCode != "")
        {
            if (maps.Current?.Points.TryGetValue(row.InitialPointCode, out var node) == true)
                vehicle.Receive(new VehicleCommand { CommandId = Guid.NewGuid().ToString("N"), VehicleId = row.VehicleId,
                    Type = VehicleCommandType.Reset, ResetPoint = new VehicleRoutePoint {
                        PointCode = node.PointCode, X = node.X, Y = node.Y, Z = node.Z, Floor = node.Floor } });
            else logger.LogWarning("车辆 {VehicleId} 的初始站点 {Point} 不在当前地图，车辆保持未定位。", row.VehicleId, row.InitialPointCode);
        }
        lock (_gate)
        {
            _vehicles.Add(row.VehicleId, new(Copy(row), vehicle));
            vehicle.StateChanged += OnStateChanged;
        }
        if (publish) PublishFleet();
    }
    public void Update(RcsVehicleRow row)
    {
        lock (_gate) _vehicles[row.VehicleId].Definition = Copy(row);
        PublishFleet();
    }
    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_vehicles.Remove(id, out var entry)) entry.Vehicle.StateChanged -= OnStateChanged;
        }
        PublishFleet();
    }
    private void OnStateChanged(VehicleStateDto state)
    {
        VehicleStateDto decorated;
        lock (_gate)
        {
            if (!_vehicles.TryGetValue(state.Id, out var entry)) return;
            decorated = Decorate(state, entry.Definition);
        }
        StateChanged?.Invoke(decorated);
    }
    private void PublishFleet() => VehiclesChanged?.Invoke(GetStates());
    private static VehicleStateDto Decorate(VehicleStateDto state, RcsVehicleRow row) => state with
        { Name = row.Name, Protocol = row.Protocol, OperatingMode = row.OperatingMode, IsEnabled = row.IsEnabled, InitialPointCode = row.InitialPointCode };
    private static RcsVehicleRow Copy(RcsVehicleRow row) => new() { VehicleId = row.VehicleId, Name = row.Name,
        Protocol = row.Protocol, OperatingMode = row.OperatingMode, InitialPointCode = row.InitialPointCode, IsEnabled = row.IsEnabled,
        CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt };
    public void Dispose()
    {
        lock (_gate) foreach (var entry in _vehicles.Values) entry.Vehicle.StateChanged -= OnStateChanged;
        _initialization.Dispose();
    }
    private sealed class Entry(RcsVehicleRow definition, IVirtualVehicle vehicle)
    {
        public RcsVehicleRow Definition { get; set; } = definition;
        public IVirtualVehicle Vehicle { get; } = vehicle;
    }
}
