namespace Contracts.Entities;

/// <summary>项目记录（每日项目日程，纯 HTTP 读写）。</summary>
public class ProjectLogDto
{
    public long Id { get; set; }
    /// <summary>所属日期（yyyy-MM-dd）。</summary>
    public string LogDate { get; set; } = "";
    /// <summary>日程内容。</summary>
    public string Content { get; set; } = "";
    /// <summary>状态：pending=待办 / done=已完成 / cancelled=搁置。</summary>
    public string Status { get; set; } = "pending";
    /// <summary>所属项目（空串=未分类，按项目隔离数据）。</summary>
    public string Project { get; set; } = "";
    /// <summary>备注（可空）。</summary>
    public string? Remark { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}