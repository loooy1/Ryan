namespace GrcsBackend.Contracts.Entities;

/// <summary>
/// 合并表记录（task_records）：一个 TaskId 的一个状态快照，替代原 ledger（台账）与 task_stage_events（阶段事件）两表。
/// stage = CREATED（WCS 下发时写，含台账字段）/ START / LOAD_FINISH / FINISHED（GRCS 阶段回调）。
/// 台账字段（TaskType / RouteCodes / CargoCode / Ok / StatusCode）仅创建行有值，阶段行留空；
/// StationCode 仅阶段行有值（GRCS 上报的当前站点），创建行用 RouteCodes 存站点对 JSON，语义分离。
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
    public List<string> RouteCodes { get; set; } = [];
    public string StationCode { get; set; } = "";
    public bool Ok { get; set; }
    public int StatusCode { get; set; }

    public bool IsCreated => string.Equals(Stage, "CREATED", StringComparison.OrdinalIgnoreCase);
}