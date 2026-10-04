namespace RCSBackend.Modules.Rcs.Application.Scheduling;

/// <summary>提供给调度策略的等待任务，不携带数据库实体。</summary>
public sealed record RcsTaskCandidate(long QueueId, string TaskId, int PriorityCode, string RequestedVehicleId = "", string Source = "UPSTREAM");
