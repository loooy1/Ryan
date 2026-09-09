namespace Contracts.Dtos;

/// <summary>Mock 规则匹配器：按来源取参后按 Op 比对 Expected。</summary>
public class MockMatcher
{
    /// <summary>参数名，如 VehicleCode / StationCode / a。</summary>
    public string Key { get; set; } = "";
    /// <summary>匹配方式：equals / contains / regex / exists。</summary>
    public string Op { get; set; } = "equals";
    /// <summary>期望值。</summary>
    public string Expected { get; set; } = "";
    /// <summary>来源：query / body / path。</summary>
    public string Source { get; set; } = "query";
}

/// <summary>通用 Mock 规则：命中则直接返回 ResponseBody。</summary>
public class MockRuleDto
{
    public string Id { get; set; } = "";
    /// <summary>名称（用于列表/记录检索显示；可空）。</summary>
    public string Name { get; set; } = "";
    public string Method { get; set; } = "POST";
    /// <summary>路径匹配，支持 * 通配，如 /api/v1/station_entry_request 或 /api/mock/*。</summary>
    public string PathPattern { get; set; } = "";
    public List<MockMatcher> Matchers { get; set; } = [];
    public int ResponseCode { get; set; } = 200;
    /// <summary>响应体 JSON 字符串（可含 {{query.key}} 占位）。</summary>
    public string ResponseBody { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 0;
    public string Description { get; set; } = "";
    /// <summary>命中后是否同时执行原业务落库（如 task_stage_change 写 task_records）。</summary>
    public bool AlsoRecord { get; set; } = true;
    /// <summary>是否关联任务看板：命中后自动从请求提取 taskId/stage 推送给看板（任意 URL 均可）。</summary>
    public bool BoardSync { get; set; } = false;
    /// <summary>是否需审批才返回最终响应（审批通过/拒绝控制 ApprovalVariable）。</summary>
    public bool RequiresApproval { get; set; } = false;
    /// <summary>审批控制的响应变量名（如 success / allow），在 ResponseBody 中以 {{approval}} 占位或直接替换该字段值。</summary>
    public string ApprovalVariable { get; set; } = "success";
    public string ApprovalTrueValue { get; set; } = "true";
    public string ApprovalFalseValue { get; set; } = "false";
}
