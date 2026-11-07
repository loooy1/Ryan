namespace Contracts.Entities;

/// <summary>项目工作区，用于保存项目档案及资料分区。</summary>
public class ProjectWorkspaceDto
{
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>项目资料分区，正文采用 Markdown 保存。</summary>
public class ProjectSectionDto
{
    public string ProjectName { get; set; } = "";
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string MarkdownContent { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}
