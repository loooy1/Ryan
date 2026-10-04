namespace RCSBackend.Modules.Rcs.Application.Scheduling;

/// <summary>调度决策；任务服务确认入库后，执行服务才启动车辆。</summary>
public sealed record RcsTaskAssignment(long QueueId, string TaskId, string VehicleId);
