namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

public sealed class RcsStationBusinessRuleRow
{
    public long Id { get; set; }
    public string MapCode { get; set; } = "default";
    public string PointCode { get; set; } = "";
    public string WaitPointCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Event { get; set; } = "BEFORE_ENTER";
    public string ActionType { get; set; } = "HTTP";
    public string ExecutionMode { get; set; } = "WAIT_FOR_RESULT";
    public string HttpMethod { get; set; } = "POST";
    public string Url { get; set; } = "";
    public string HeadersJson { get; set; } = "{}";
    public string RequestBodyTemplate { get; set; } = "{}";
    public string PermitResponsePath { get; set; } = "allowPass";
    public string DenyMessagePath { get; set; } = "message";
    public int TimeoutMs { get; set; } = 5000;
    public int RetryCount { get; set; }
    public int RetryDelayMs { get; set; } = 1000;
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
