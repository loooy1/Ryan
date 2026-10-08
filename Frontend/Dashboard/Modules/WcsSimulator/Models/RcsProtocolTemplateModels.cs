using Contracts.Dtos;

namespace Dashboard.Modules.WcsSimulator.Models;

public sealed class RcsProtocolProfilesDto
{
    public int SchemaVersion { get; set; }
    public string ActiveProfileId { get; set; } = "RCS1";
    public List<RcsProtocolProfileDto> Profiles { get; set; } = [];
}

public sealed class RcsProtocolProfileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public List<RcsProtocolOperationTemplateDto> Operations { get; set; } = [];
    public List<FeatureModuleDto> FeatureModules { get; set; } = [];
    public List<MockRuleDto> MockRules { get; set; } = [];
}

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
