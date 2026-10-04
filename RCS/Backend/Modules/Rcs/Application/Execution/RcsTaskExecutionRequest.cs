namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>任务服务交给执行服务的执行要求；不依赖任务表实体。</summary>
public sealed record RcsTaskExecutionRequest(string TaskId, string VehicleId, string MapCode,
    string Warehouse, IReadOnlyList<string> StationCodes, string TaskType = "MOVE_ONLY", string ContainerCode = "",
    IReadOnlyList<string>? StationActions = null);
