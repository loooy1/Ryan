namespace Contracts.Entities;

public class ModuleExecLogRow
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ExecutionPhase { get; set; } = "";
    public string Status { get; set; } = "";
    public int HttpStatus { get; set; }
    public string DetailJson { get; set; } = "{}";
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
}
