using Contracts.Dtos;

namespace WCSBackend.Modules.Wcs.Infrastructure.Persistence.Entities;

/// <summary>按 RCS 类型隔离的 Mock 入站模板。</summary>
public sealed class RcsMockRuleRow
{
    public string RcsType { get; set; } = "RCS1";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Method { get; set; } = "";
    public string PathPattern { get; set; } = "";
    public List<MockMatcher> Matchers { get; set; } = [];
    public int ResponseCode { get; set; }
    public string ResponseBody { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public string Description { get; set; } = "";
    public bool AlsoRecord { get; set; }
    public bool BoardSync { get; set; }
    public bool RequiresApproval { get; set; }
    public string ApprovalVariable { get; set; } = "";
    public string ApprovalTrueValue { get; set; } = "";
    public string ApprovalFalseValue { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}
