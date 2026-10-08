using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Models;

/// <summary>RCS 模板集合的 API 模型；持久化到 WCS 的 Mock、业务模板和任务协议三张表。</summary>
public sealed class RcsProtocolProfilesDto
{
    public int SchemaVersion { get; set; }
    public string ActiveProfileId { get; set; } = "RCS1";
    public List<RcsProtocolProfileDto> Profiles { get; set; } = [];
}

/// <summary>一个 RCS 类型的完整配置：地址、协议、功能模板和 Mock 入站。</summary>
public sealed class RcsProtocolProfileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public List<RcsProtocolOperationTemplateDto> Operations { get; set; } = [];
    public List<FeatureModuleDto> FeatureModules { get; set; } = [];
    public List<MockRuleDto> MockRules { get; set; } = [];
}

/// <summary>单个 HTTP 协议操作模板。</summary>
public sealed class RcsProtocolOperationTemplateDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string HttpMethod { get; set; } = "POST";
    public string ApiPath { get; set; } = "";
    public string RequestBodyTemplate { get; set; } = "";
    public string SuccessProperty { get; set; } = "";
    public string SuccessValue { get; set; } = "true";
    public int TimeoutSeconds { get; set; } = 10;
}
