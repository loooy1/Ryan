using GrcsBackend.Contracts.Dtos;

namespace GrcsBackend.Contracts.Entities;

/// <summary>模拟规则行（mock_rules 表，固定属性全部列化 + Matchers JSON 列）。</summary>
public class MockRuleRow
{
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