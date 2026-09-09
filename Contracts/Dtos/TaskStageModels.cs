namespace Contracts.Dtos;

/// <summary>
/// 任务阶段变化通知（GRCS TaskStageChange 出站调用）。
/// Stage 枚举：START / LOAD_FINISH / FINISHED（用 string 宽松接收，兼容后续新增阶段）。
/// StationCode / ContainerCode 为可选：任务结束时附加容器的实际放货位置与货物编码
/// （"" 无货，"Unknown" 有货但未扫到码）。
/// </summary>
public class TaskStageChangeModel
{
    public DateTime MsgTime { get; set; }
    public string Warehouse { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string StationCode { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string Stage { get; set; } = "";
}

/// <summary>阶段变化事件（给 WCS 前端展示，/api/wcs/task-stages）。</summary>
public class StageChangeEvent
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    /// <summary>任务类型（后端不填；前端阶段事件页从台账按 TaskId 回填，仅展示用）。</summary>
    public string TaskType { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public string StationCode { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string Stage { get; set; } = "";
    public DateTime Time { get; set; }
}
