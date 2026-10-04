namespace Contracts.Rcs.Vehicle;

public sealed record VehicleStateDto
{
    public string Id { get; init; } = "V-01";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "VIRTUAL";
    public string OperatingMode { get; init; } = "AUTO";
    public bool IsEnabled { get; init; } = true;
    public string InitialPointCode { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string CommandId { get; init; } = "";
    public int RouteVersion { get; init; }
    public string LoadedContainerCode { get; init; } = "";
    public string LastAction { get; init; } = "move";
    public IReadOnlyList<string> CompletedStepIds { get; init; } = [];
    public string PointCode { get; init; } = "";
    /// <summary>Last point in the route segment currently accepted by the vehicle.</summary>
    public string LockPointCode { get; init; } = "";
    /// <summary>Current and upcoming points still reserved by the traffic planner; passed points are removed as progress is confirmed.</summary>
    public IReadOnlyList<string> LockedPointCodes { get; init; } = [];
    /// <summary>Map lines in the route window exclusively reserved by the traffic planner.</summary>
    public IReadOnlyList<string> LockedLineCodes { get; init; } = [];
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public int Floor { get; init; }
    public string Status { get; init; } = "Idle";
    public int RouteIndex { get; init; }
    public int RouteLength { get; init; }
}
