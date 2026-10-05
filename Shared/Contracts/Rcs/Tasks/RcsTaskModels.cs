namespace Contracts.Rcs.Tasks;

/// <summary>上游 POST /api/v1/task_receive 的公开报文契约。</summary>
public sealed class RcsUpstreamTaskReceiveRequest
{
    public string GroupId { get; set; } = "";
    public string MsgTime { get; set; } = "";
    public int PriorityCode { get; set; } = 1;
    public string Warehouse { get; set; } = "";
    public List<RcsUpstreamTaskRequest> Tasks { get; set; } = [];
}

/// <summary>上游任务字段；车辆分配和站点动作由 RCS 内部处理。</summary>
public sealed class RcsUpstreamTaskRequest
{
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public List<string> StationCode { get; set; } = [];
    public List<string> AreaCode { get; set; } = [];
}

public sealed class RcsTaskReceiveRequest
{
    public string GroupId { get; set; } = "";
    public string MsgTime { get; set; } = "";
    public int PriorityCode { get; set; } = 1;
    public string Warehouse { get; set; } = "";
    public List<RcsTaskRequest> Tasks { get; set; } = [];
}

public sealed class RcsTaskRequest
{
    public string TaskId { get; set; } = "";
    public string VehicleId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public List<string> StationCode { get; set; } = [];
    /// <summary>Optional action for each station, aligned with StationCode; values are move, fetch, or put.</summary>
    public List<string> StationActions { get; set; } = [];
    public List<string> AreaCode { get; set; } = [];
}

public static class RcsTaskStatus
{
    public const string Waiting = "WAITING";
    public const string Running = "RUNNING";
    public const string Paused = "PAUSED";
    public const string Cancelling = "CANCELLING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Interrupted = "INTERRUPTED";
}

public static class RcsTaskExecutionStageStatus
{
    public const string Waiting = "WAITING";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>一个上游任务在 RCS 内部执行的持久化阶段。</summary>
public sealed record RcsTaskExecutionStageDto
{
    public int Sequence { get; init; }
    public string StageCode { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = RcsTaskExecutionStageStatus.Waiting;
    public string FromPointCode { get; init; } = "";
    public string ToPointCode { get; init; } = "";
    public string Action { get; init; } = "";
    public string VehicleId { get; init; } = "";
    public string CommandId { get; init; } = "";
    public string Message { get; init; } = "";
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
}

public sealed class RcsTaskDto
{
    public string TaskId { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string MsgTime { get; init; } = "";
    public string Warehouse { get; init; } = "";
    public int PriorityCode { get; init; }
    public string TaskType { get; init; } = "";
    public string ContainerCode { get; init; } = "";
    public IReadOnlyList<string> StationCode { get; init; } = [];
    public IReadOnlyList<string> StationActions { get; init; } = [];
    public IReadOnlyList<string> AreaCode { get; init; } = [];
    public string Status { get; init; } = RcsTaskStatus.Waiting;
    public string Source { get; init; } = RcsTaskSource.Upstream;
    public string VehicleId { get; init; } = "";
    public string RequestedVehicleId { get; init; } = "";
    public string MapCode { get; init; } = "";
    public int MapVersion { get; init; }
    public IReadOnlyList<string> RoutePointCodes { get; init; } = [];
    public IReadOnlyList<RcsTaskExecutionStageDto> ExecutionStages { get; init; } = [];
    public string OriginalRequestJson { get; init; } = "";
    public string Message { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
}

public sealed class RcsTaskReceiveResponse
{
    public bool Success { get; init; }
    public string MsgTime { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<RcsTaskDto> Tasks { get; init; } = [];
}
