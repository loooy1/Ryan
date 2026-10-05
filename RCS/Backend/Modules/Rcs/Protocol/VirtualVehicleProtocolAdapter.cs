using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;
using Rcs.VirtualVehicle;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using System.Text.Json;

namespace RCSBackend.Modules.Rcs.Protocol;

/// <summary>当前内置协议：进程内虚拟车。物理车型后续各自实现 IVehicleProtocolAdapter。</summary>
public sealed class VirtualVehicleProtocolAdapter : IVehicleProtocolAdapter
{
    public string ProtocolCode => "VIRTUAL";
    public string DisplayName => "虚拟车协议";
    public bool RequiresHeartbeat => false;

    public VehicleHeartbeatTelemetry DecodeHeartbeat(ReadOnlyMemory<byte> payload) =>
        JsonSerializer.Deserialize<VehicleHeartbeatTelemetry>(payload.Span,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new JsonException("心跳报文为空或不是有效 JSON。");

    public IVehicleProtocolSession CreateSession(RcsVehicleRow definition, VehicleRoutePoint? initialPosition) =>
        new Session(definition.VehicleId, initialPosition);

    private sealed class Session : IVehicleProtocolSession
    {
        private readonly IVirtualVehicle _vehicle;

        public Session(string vehicleId, VehicleRoutePoint? initialPosition)
        {
            _vehicle = new VirtualVehicleSimulator(vehicleId);
            _vehicle.StateChanged += ForwardState;
            if (initialPosition is not null)
                _vehicle.Receive(new VehicleCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"), VehicleId = vehicleId,
                    Type = VehicleCommandType.Reset, ResetPoint = initialPosition
                });
        }

        public string VehicleId => _vehicle.State.Id;
        public VehicleStateDto State => _vehicle.State;
        public event Action<VehicleStateDto>? StateChanged;
        public Task<VehicleCommandAck> SendAsync(VehicleCommand command, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(_vehicle.Receive(command));
        }
        public Task<VehicleCommandResult> WaitForCompletionAsync(string commandId, CancellationToken token = default) =>
            _vehicle.WaitForCompletionAsync(commandId, token);
        private void ForwardState(VehicleStateDto state) => StateChanged?.Invoke(state);
        public void Dispose() => _vehicle.StateChanged -= ForwardState;
    }
}
