namespace Contracts.Entities;

/// <summary>信号确认状态行（workflow_state 表）。kind = arrival / removal / sent。</summary>
public class WorkflowStateRow
{
    public string Kind { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string? Value { get; set; }
    public string Time { get; set; } = "";
}