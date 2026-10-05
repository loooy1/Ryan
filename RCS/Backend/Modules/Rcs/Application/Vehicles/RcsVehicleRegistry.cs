using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;
using Rcs.VirtualVehicle;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;
using RCSBackend.Modules.Rcs.Protocol;
using System.Text.Json;

namespace RCSBackend.Modules.Rcs.Application.Vehicles;

/// <summary>车辆实例和配置缓存。只有协议通道接触车辆对象；配置变更由车辆管理服务协调。</summary>
public sealed class RcsVehicleRegistry(IRcsVehicleStore store, RcsMapCache maps,
    IEnumerable<IVehicleProtocolAdapter> adapters, ILogger<RcsVehicleRegistry> logger) : IDisposable
{
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly Dictionary<string, Entry> _vehicles = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, IVehicleProtocolAdapter> _adapters = adapters
        .ToDictionary(x => x.ProtocolCode, StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    public event Action<VehicleStateDto>? StateChanged;
    public event Action<IReadOnlyList<VehicleStateDto>>? VehiclesChanged;
    public IReadOnlyList<VehicleProtocolInfoDto> SupportedProtocols => _adapters.Values
        .OrderBy(x => x.ProtocolCode, StringComparer.Ordinal)
        .Select(x => new VehicleProtocolInfoDto(x.ProtocolCode, x.DisplayName)).ToArray();
    public bool SupportsProtocol(string? code) => !string.IsNullOrWhiteSpace(code) && _adapters.ContainsKey(code.Trim());

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
        lock (_gate) return _vehicles.Values.Select(x => Decorate(x.Vehicle.State, x))
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
    public IVehicleProtocolSession GetVehicle(string id)
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

    public VehicleStateDto ReceiveHeartbeat(string id, ReadOnlyMemory<byte> payload)
    {
        Entry entry;
        IVehicleProtocolAdapter adapter;
        lock (_gate)
        {
            if (!_vehicles.TryGetValue(id, out entry!)) throw new ArgumentException($"车辆 {id} 不存在。");
            if (!_adapters.TryGetValue(entry.Definition.Protocol, out adapter!))
                throw new ArgumentException($"车辆 {id} 的协议 {entry.Definition.Protocol} 未注册心跳解析器。");
        }

        VehicleHeartbeatTelemetry telemetry;
        try { telemetry = adapter.DecodeHeartbeat(payload); }
        catch (JsonException ex) { throw new ArgumentException($"车辆心跳报文格式无效：{ex.Message}"); }
        ValidateHeartbeat(telemetry);
        if (telemetry.PointCode != "" && maps.Current is { } map && !map.Points.ContainsKey(telemetry.PointCode))
            throw new ArgumentException($"车辆心跳上报的站点 {telemetry.PointCode} 不在当前地图中。");

        VehicleStateDto state;
        lock (_gate)
        {
            if (!_vehicles.TryGetValue(id, out var current) || !ReferenceEquals(entry, current))
                throw new ArgumentException($"车辆 {id} 的协议会话已变更，请重新发送心跳。");
            entry.Telemetry = telemetry;
            entry.LastHeartbeatAt = DateTimeOffset.UtcNow;
            entry.IsOnline = true;
            state = Decorate(entry.Vehicle.State, entry);
        }
        StateChanged?.Invoke(state);
        return state;
    }

    public void ExpireHeartbeats()
    {
        List<VehicleStateDto> offline = [];
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var entry in _vehicles.Values)
            {
                if (!entry.RequiresHeartbeat || !entry.IsOnline || entry.LastHeartbeatAt is not { } last
                    || now - last <= HeartbeatTimeout) continue;
                entry.IsOnline = false;
                offline.Add(Decorate(entry.Vehicle.State, entry));
            }
        }
        foreach (var state in offline) StateChanged?.Invoke(state);
    }

    public void Add(RcsVehicleRow row, bool publish = true)
    {
        var vehicle = CreateSession(row, row.InitialPointCode);
        lock (_gate)
        {
            _vehicles.Add(row.VehicleId, new(Copy(row), vehicle, RequiresHeartbeat(row.Protocol)));
            vehicle.StateChanged += OnStateChanged;
        }
        if (publish) PublishFleet();
    }
    public void Update(RcsVehicleRow row)
    {
        Entry old;
        lock (_gate) old = _vehicles[row.VehicleId];
        if (string.Equals(old.Definition.Protocol, row.Protocol, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate) old.Definition = Copy(row);
        }
        else
        {
            var location = old.Vehicle.State.PointCode;
            var replacement = CreateSession(row, location);
            lock (_gate)
            {
                old.Vehicle.StateChanged -= OnStateChanged;
                _vehicles[row.VehicleId] = new(Copy(row), replacement, RequiresHeartbeat(row.Protocol));
                replacement.StateChanged += OnStateChanged;
            }
            old.Vehicle.Dispose();
        }
        PublishFleet();
    }
    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_vehicles.Remove(id, out var entry))
            {
                entry.Vehicle.StateChanged -= OnStateChanged;
                entry.Vehicle.Dispose();
            }
        }
        PublishFleet();
    }
    private void OnStateChanged(VehicleStateDto state)
    {
        VehicleStateDto decorated;
        lock (_gate)
        {
            if (!_vehicles.TryGetValue(state.Id, out var entry)) return;
            decorated = Decorate(state, entry);
        }
        StateChanged?.Invoke(decorated);
    }
    private void PublishFleet() => VehiclesChanged?.Invoke(GetStates());
    private static VehicleStateDto Decorate(VehicleStateDto state, Entry entry)
    {
        var row = entry.Definition;
        var telemetry = entry.Telemetry;
        return state with
        {
            Name = row.Name, Protocol = row.Protocol, OperatingMode = row.OperatingMode,
            IsEnabled = row.IsEnabled, InitialPointCode = row.InitialPointCode,
            IsOnline = state.Status != "ProtocolUnavailable"
                && (!entry.RequiresHeartbeat || (entry.IsOnline && entry.LastHeartbeatAt is { } last
                    && DateTimeOffset.UtcNow - last <= HeartbeatTimeout)),
            LastHeartbeatAt = entry.LastHeartbeatAt,
            PointCode = string.IsNullOrWhiteSpace(telemetry?.PointCode) ? state.PointCode : telemetry.PointCode,
            X = telemetry?.X ?? state.X, Y = telemetry?.Y ?? state.Y, Z = telemetry?.Z ?? state.Z,
            Floor = telemetry?.Floor ?? state.Floor,
            Status = string.IsNullOrWhiteSpace(telemetry?.Status) ? state.Status : telemetry.Status,
            TaskId = string.IsNullOrWhiteSpace(telemetry?.TaskId) ? state.TaskId : telemetry.TaskId,
            CommandId = string.IsNullOrWhiteSpace(telemetry?.CommandId) ? state.CommandId : telemetry.CommandId,
            RouteVersion = telemetry?.RouteVersion ?? state.RouteVersion,
            RouteIndex = telemetry?.RouteIndex ?? state.RouteIndex,
            RouteLength = telemetry?.RouteLength ?? state.RouteLength,
            LoadedContainerCode = string.IsNullOrWhiteSpace(telemetry?.LoadedContainerCode)
                ? state.LoadedContainerCode : telemetry.LoadedContainerCode,
            BatteryPercent = telemetry?.BatteryPercent ?? state.BatteryPercent,
            ErrorCode = telemetry?.ErrorCode ?? state.ErrorCode,
            ErrorMessage = telemetry?.ErrorMessage ?? state.ErrorMessage
        };
    }

    private static void ValidateHeartbeat(VehicleHeartbeatTelemetry? telemetry)
    {
        if (telemetry is null) throw new ArgumentException("车辆心跳解析结果为空。");
        if (telemetry.BatteryPercent is < 0 or > 100) throw new ArgumentException("心跳电量必须在 0 到 100 之间。");
        if (telemetry.X is { } x && !double.IsFinite(x) || telemetry.Y is { } y && !double.IsFinite(y)
            || telemetry.Z is { } z && !double.IsFinite(z))
            throw new ArgumentException("心跳坐标必须是有限数值。");
        if (telemetry.RouteVersion is < 0 || telemetry.RouteIndex is < 0 || telemetry.RouteLength is < 0)
            throw new ArgumentException("心跳路径版本或进度不能为负数。");
        if (!string.IsNullOrWhiteSpace(telemetry.Status)
            && telemetry.Status is not ("Idle" or "Running" or "Paused" or "Arrived" or "Error" or "Offline"))
            throw new ArgumentException("心跳状态必须映射为 Idle、Running、Paused、Arrived、Error 或 Offline。");
    }

    private bool RequiresHeartbeat(string protocol) =>
        _adapters.TryGetValue(protocol, out var adapter) ? adapter.RequiresHeartbeat : true;
    private static RcsVehicleRow Copy(RcsVehicleRow row) => new() { VehicleId = row.VehicleId, Name = row.Name,
        Protocol = row.Protocol, OperatingMode = row.OperatingMode, InitialPointCode = row.InitialPointCode, IsEnabled = row.IsEnabled,
        CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt };
    public void Dispose()
    {
        lock (_gate) foreach (var entry in _vehicles.Values)
        {
            entry.Vehicle.StateChanged -= OnStateChanged;
            entry.Vehicle.Dispose();
        }
        _initialization.Dispose();
    }
    private IVehicleProtocolSession CreateSession(RcsVehicleRow row, string? pointCode)
    {
        VehicleRoutePoint? position = null;
        if (!string.IsNullOrWhiteSpace(pointCode) && maps.Current?.Points.TryGetValue(pointCode, out var node) == true)
            position = new VehicleRoutePoint { PointCode = node.PointCode, X = node.X, Y = node.Y, Z = node.Z, Floor = node.Floor };
        else if (!string.IsNullOrWhiteSpace(pointCode))
            logger.LogWarning("车辆 {VehicleId} 的初始站点 {Point} 不在当前地图，车辆保持未定位。", row.VehicleId, pointCode);
        if (!_adapters.TryGetValue(row.Protocol, out var adapter))
        {
            logger.LogError("车辆 {VehicleId} 配置了未注册的协议适配器 {Protocol}，该车将保持离线。", row.VehicleId, row.Protocol);
            return new MissingProtocolSession(row.VehicleId, row.Protocol, position);
        }
        return adapter.CreateSession(row, position);
    }
    private sealed class MissingProtocolSession(string vehicleId, string protocol, VehicleRoutePoint? position) : IVehicleProtocolSession
    {
        public string VehicleId => vehicleId;
        public VehicleStateDto State => new()
        {
            Id = vehicleId, Protocol = protocol, Status = "ProtocolUnavailable",
            PointCode = position?.PointCode ?? "", X = position?.X ?? 0, Y = position?.Y ?? 0,
            Z = position?.Z ?? 0, Floor = position?.Floor ?? 0
        };
        public event Action<VehicleStateDto>? StateChanged { add { } remove { } }
        public Task<VehicleCommandAck> SendAsync(VehicleCommand command, CancellationToken token = default) =>
            Task.FromResult(new VehicleCommandAck(command.CommandId, false, $"协议 {protocol} 尚未注册对应适配器。"));
        public Task<VehicleCommandResult> WaitForCompletionAsync(string commandId, CancellationToken token = default) =>
            Task.FromResult(new VehicleCommandResult(commandId, "", 0, "FAILED", "协议适配器不可用。"));
        public void Dispose() { }
    }
    private sealed class Entry(RcsVehicleRow definition, IVehicleProtocolSession vehicle, bool requiresHeartbeat)
    {
        public RcsVehicleRow Definition { get; set; } = definition;
        public IVehicleProtocolSession Vehicle { get; } = vehicle;
        public bool RequiresHeartbeat { get; } = requiresHeartbeat;
        public bool IsOnline { get; set; } = !requiresHeartbeat;
        public DateTimeOffset? LastHeartbeatAt { get; set; }
        public VehicleHeartbeatTelemetry? Telemetry { get; set; }
    }
}
