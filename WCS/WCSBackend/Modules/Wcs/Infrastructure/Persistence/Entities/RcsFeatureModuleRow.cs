using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Infrastructure.Persistence.Entities;

/// <summary>按 RCS 类型隔离的业务功能模板。</summary>
public sealed class RcsFeatureModuleRow
{
    public string RcsType { get; set; } = "RCS1";
    public string Id { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool IsActive { get; set; }
    public string Name { get; set; } = "";
    public string TaskInterfaceType { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string HttpMethod { get; set; } = "POST";
    public string RequestBodyTemplate { get; set; } = "";
    public string SuccessProperty { get; set; } = "";
    public string SuccessValue { get; set; } = "true";
    public int TimeoutSeconds { get; set; } = 10;
    public string PreExecutionEffect { get; set; } = "";
    public string InventoryEffect { get; set; } = "";
    public List<WorkParamDto> Params { get; set; } = [];
    public string UpdatedAt { get; set; } = "";
}
