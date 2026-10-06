namespace Contracts.Rcs.StationBusiness;

public static class RcsStationEvents
{
    public const string BeforeEnter = "BEFORE_ENTER";
    public const string AfterEnter = "AFTER_ENTER";
    public const string BeforeAction = "BEFORE_ACTION";
    public const string AfterAction = "AFTER_ACTION";
    public const string BeforeLeave = "BEFORE_LEAVE";
    public const string AfterLeave = "AFTER_LEAVE";

    public static readonly IReadOnlyList<string> All =
        [BeforeEnter, AfterEnter, BeforeAction, AfterAction, BeforeLeave, AfterLeave];
}

public static class RcsStationRuleModes
{
    public const string WaitForResult = "WAIT_FOR_RESULT";
    public const string FireAndForget = "FIRE_AND_FORGET";
}

public sealed class RcsStationBusinessRuleDto
{
    public long Id { get; set; }
    public string MapCode { get; set; } = "default";
    public string PointCode { get; set; } = "";
    public string WaitPointCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Event { get; set; } = RcsStationEvents.BeforeEnter;
    public string ActionType { get; set; } = "HTTP";
    public string ExecutionMode { get; set; } = RcsStationRuleModes.WaitForResult;
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
    public string UpdatedAt { get; set; } = "";
}

public sealed class SaveRcsStationBusinessRuleRequest
{
    public string MapCode { get; set; } = "default";
    public string PointCode { get; set; } = "";
    public string WaitPointCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Event { get; set; } = RcsStationEvents.BeforeEnter;
    public string ActionType { get; set; } = "HTTP";
    public string ExecutionMode { get; set; } = RcsStationRuleModes.WaitForResult;
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
}

public sealed record RcsStationRuleExecutionContext(
    string TaskId,
    string VehicleId,
    string MapCode,
    string Warehouse,
    string PointCode,
    string Event,
    string ContainerCode = "",
    string Action = "",
    string WaitPointCode = "");
