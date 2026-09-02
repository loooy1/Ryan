namespace GrcsBackend.Contracts.Dtos;

/// <summary>Mock 审批事件（运行时模型 + SignalR 负载 + mock_request_events 表映射，三合一）。</summary>
public class MockRequestEventDto
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string PathPattern { get; set; } = "";
    public string Method { get; set; } = "";
    public string BodyJson { get; set; } = "";
    public string QueryString { get; set; } = "";
    public DateTime Time { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public string MockRuleId { get; set; } = "";
    public string MockRuleDescription { get; set; } = "";
    /// <summary>命中该事件的 Mock 卡片完整配置快照（Method/PathPattern/Matchers/ResponseCode/ResponseBody/审批变量/批准拒绝值等），供前端「请求信号」展开查看。</summary>
    public string RuleJson { get; set; } = "";
}