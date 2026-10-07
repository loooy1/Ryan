using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;

namespace RCSBackend.Modules.Rcs.Protocol;

/// <summary>一个可选车型协议的工厂。每辆车拥有独立会话，协议适配器负责真实报文的编解码。</summary>
public interface IVehicleProtocolAdapter
{
    string ProtocolCode { get; }
    string DisplayName { get; }
    bool RequiresHeartbeat { get; }
    VehicleHeartbeatTelemetry DecodeHeartbeat(ReadOnlyMemory<byte> payload);
    IVehicleProtocolSession CreateSession(VehicleProtocolDefinition definition, VehicleRoutePoint? initialPosition);
}

/// <summary>传给车型适配器的车辆配置，不暴露数据库实体及其生命周期。</summary>
public sealed record VehicleProtocolDefinition(
    string VehicleId, string Name, string Protocol, string OperatingMode,
    string InitialPointCode, bool IsEnabled);

/// <summary>
/// 统一车辆会话。适配器只有在车辆确认动作执行完成后，才能报告对应 CompletedStepIds、LoadedContainerCode 和完成结果；
/// Accepted 只表示命令接收，不能当作取放货成功。
/// </summary>
public interface IVehicleProtocolSession : IDisposable
{
    string VehicleId { get; }
    VehicleStateDto State { get; }
    event Action<VehicleStateDto>? StateChanged;
    Task<VehicleCommandAck> SendAsync(VehicleCommand command, CancellationToken token = default);
    Task<VehicleCommandResult> WaitForCompletionAsync(string commandId, CancellationToken token = default);
}
