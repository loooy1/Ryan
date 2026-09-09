namespace Contracts.Entities;

/// <summary>模块执行记录持久化行（module_exec_logs 表）。</summary>
public class ModuleExecLogRow
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string Point { get; set; } = "";
    public string Module { get; set; } = "";
    public bool Ok { get; set; }
    public int HttpCode { get; set; }
    public string Detail { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}