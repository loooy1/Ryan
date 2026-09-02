namespace GrcsBackend.Contracts.Entities;

/// <summary>异常记录（AGV/软件异常台账，纯 HTTP 读写）。</summary>
public class ExceptionRecordDto
{
    public long Id { get; set; }
    /// <summary>发生时间（ISO 字符串，yyyy-MM-dd HH:mm:ss）。</summary>
    public string HappenedAt { get; set; } = "";
    /// <summary>车号（可空，记录是哪台车出的问题）。</summary>
    public string? VehicleCode { get; set; }
    /// <summary>现象。</summary>
    public string Phenomenon { get; set; } = "";
    /// <summary>原因。</summary>
    public string Reason { get; set; } = "";
    /// <summary>处理进度（可空，自由文本）。</summary>
    public string? Progress { get; set; }
    /// <summary>责任部门（必填，RCS / WCS / Quicktron）。</summary>
    public string ResponsibleDept { get; set; } = "";
    /// <summary>状态：resolved=已解决 / pending=未回复 / in_progress=进行中 / observing=修复待观察。</summary>
    public string Status { get; set; } = "pending";
    /// <summary>所属项目（空串=未分类，按项目隔离数据）。</summary>
    public string Project { get; set; } = "";
    /// <summary>最近复现时间（可空）。</summary>
    public string? ReproducedAt { get; set; }
    /// <summary>复现次数。</summary>
    public int ReproduceCount { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}