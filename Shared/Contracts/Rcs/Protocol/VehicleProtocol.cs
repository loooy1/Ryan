namespace Contracts.Rcs.Protocol;

public static class VehicleCommandType
{
    public const string Move = "MOVE";
    public const string ExecuteAction = "EXECUTE_ACTION";
    public const string UpdateRoute = "UPDATE_ROUTE";
    public const string SlideRoute = "SLIDE_ROUTE";
    public const string Stop = "STOP";
    public const string Pause = "PAUSE";
    public const string Resume = "RESUME";
    public const string Reset = "RESET";
}

public static class VehiclePointAction
{
    public const string Move = "move";
    public const string Fetch = "fetch";
    public const string Put = "put";
}

/// <summary>车只接收执行点，不接收地图图结构。StepId 标识业务站点，改路后保持不变。</summary>
public sealed record VehicleRoutePoint
{
    public string PointCode { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public int Floor { get; init; }
    public string Action { get; init; } = VehiclePointAction.Move;
    public string StepId { get; init; } = "";
}

public sealed record VehicleCommand
{
    public string CommandId { get; init; } = "";
    public string VehicleId { get; init; } = "V-01";
    public string TaskId { get; init; } = "";
    public string Type { get; init; } = VehicleCommandType.Move;
    /// <summary>Standalone station action; used only with EXECUTE_ACTION, never embedded in path points.</summary>
    public string Action { get; init; } = "";
    public string ActionStepId { get; init; } = "";
    public int RouteVersion { get; init; }
    /// <summary>Zero-based index of Points[0] in the complete planned route.</summary>
    public int RoutePointOffset { get; init; }
    /// <summary>Total points in the complete route; zero preserves legacy single-window commands.</summary>
    public int TotalRoutePoints { get; init; }
    /// <summary>The vehicle must wait for a subsequent SLIDE_ROUTE after this window.</summary>
    public bool HasMorePoints { get; init; }
    public string ContainerCode { get; init; } = "";
    public bool StartPaused { get; init; }
    public string ExpectedPointCode { get; init; } = "";
    public IReadOnlyList<VehicleRoutePoint> Points { get; init; } = [];
    public VehicleRoutePoint? ResetPoint { get; init; }
}

/// <summary>接收确认与执行完成分开；Accepted 不代表已到终点或已取放货。</summary>
public sealed record VehicleCommandAck(string CommandId, bool Accepted, string Message);

/// <summary>Status 为 COMPLETED、STOPPED 或 FAILED，CommandId 始终为最初 MOVE 的 ID。</summary>
public sealed record VehicleCommandResult(string CommandId, string TaskId, int RouteVersion,
    string Status, string Message);
