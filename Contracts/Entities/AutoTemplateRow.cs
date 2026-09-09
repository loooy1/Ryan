using Contracts.Dtos;

namespace Contracts.Entities;

/// <summary>自动化模板行（auto_templates 表，固定属性列化 + Steps JSON 列）。</summary>
public class AutoTemplateRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<AutoStepDto> Steps { get; set; } = [];
    public string UpdatedAt { get; set; } = "";
}