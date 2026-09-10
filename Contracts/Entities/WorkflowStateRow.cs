namespace Contracts.Entities;

/// <summary>信号确认接口的兼容返回项；数据由 task_records 派生。</summary>
public class WorkflowStateRow
{
    public string Kind { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string? Value { get; set; }
    public string Time { get; set; } = "";
}
