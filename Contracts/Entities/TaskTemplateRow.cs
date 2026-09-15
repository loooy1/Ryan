using Contracts.Dtos;

namespace Contracts.Entities;

/// <summary>任务模板行（task_templates 表，固定属性列化 + Start/End JSON 列）。</summary>
public class TaskTemplateRow
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public bool NeedsContainer { get; set; }
    public string ContainerPrefix { get; set; } = "";
    public bool RandomContainer { get; set; }
    public string ContainerMode { get; set; } = TaskContainerModes.ExistingInventory;
    public TaskPointDto Start { get; set; } = new();
    public TaskPointDto End { get; set; } = new();
    public string UpdatedAt { get; set; } = "";
}