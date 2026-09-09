using Contracts.Dtos;

namespace Contracts.Entities;

/// <summary>功能模块行（feature_modules 表，固定属性列化 + Params JSON 列）。</summary>
public class FeatureModuleRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public List<WorkParamDto> Params { get; set; } = [];
    public string UpdatedAt { get; set; } = "";
}