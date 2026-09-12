namespace Contracts.Entities;

/// <summary>
/// 合并表记录（task_records）：一个 TaskId 的一个状态快照，替代原 ledger（台账）与 task_stage_events（阶段事件）两表。
/// stage = CREATED（WCS 下发时写，含台账字段）/ START / LOAD_FINISH / FINISHED（GRCS 阶段回调）。
/// 每行都保存任务的起点、终点、托盘和货物，使任意阶段都能独立驱动库存流转。
/// </summary>
public class TaskRecord
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string Stage { get; set; } = "";
    public DateTime Time { get; set; }
    public string Warehouse { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string CargoCode { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string StartStationCode { get; set; } = "";
    public string EndStationCode { get; set; } = "";
    public string StageStatus { get; set; } = TaskStageStatuses.Success;
    public int StatusCode { get; set; }

    public bool IsCreated => string.Equals(Stage, "CREATED", StringComparison.OrdinalIgnoreCase);
    public bool IsSuccess => string.Equals(StageStatus, TaskStageStatuses.Success, StringComparison.OrdinalIgnoreCase);
}

public static class TaskStageStatuses
{
    public const string Success = "success";
    public const string Fail = "fail";
    public const string Callback = "callback";
}
