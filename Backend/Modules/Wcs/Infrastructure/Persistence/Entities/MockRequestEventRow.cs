namespace Contracts.Entities;

/// <summary>请求信号事件持久化行（mock_request_events 表）。</summary>
public class MockRequestEventRow
{
    public long EventId { get; set; }
    public string Key { get; set; } = "";
    public string PathPattern { get; set; } = "";
    public string Method { get; set; } = "";
    public string BodyJson { get; set; } = "";
    public string QueryString { get; set; } = "";
    public string Time { get; set; } = "";
    public string? DecidedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public string MockRuleId { get; set; } = "";
    public string MockRuleDescription { get; set; } = "";
    public string RuleJson { get; set; } = "";
}