namespace Contracts.Rcs.Vehicle;

/// <summary>
/// Normalized data decoded from a vehicle-specific heartbeat by its protocol adapter.
/// Device protocol adapters map their wire fields into this shared model.
/// </summary>
public sealed record VehicleHeartbeatTelemetry
{
    public string PointCode { get; init; } = "";
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public string Status { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string CommandId { get; init; } = "";
    public int? RouteVersion { get; init; }
    public int? RouteIndex { get; init; }
    public int? RouteLength { get; init; }
    public string LoadedContainerCode { get; init; } = "";
    public double? BatteryPercent { get; init; }
    public string ErrorCode { get; init; } = "";
    public string ErrorMessage { get; init; } = "";
}
