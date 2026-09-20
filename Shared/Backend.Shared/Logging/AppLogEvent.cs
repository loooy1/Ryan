namespace Backend.Shared.Logging;

public sealed class AppLogEvent
{
    public DateTimeOffset Time { get; init; }
    public string Level { get; init; } = "";
    public string System { get; init; } = "";
    public string Category { get; init; } = "System";
    public string Component { get; init; } = "";
    public string SourceContext { get; init; } = "";
    public string EventName { get; init; } = "";
    public string TraceId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string RoundId { get; init; } = "";
    public string ModuleId { get; init; } = "";
    public string StationCode { get; init; } = "";
    public string ContainerCode { get; init; } = "";
    public string CargoCode { get; init; } = "";
    public string Message { get; init; } = "";
    public Dictionary<string, object?> Data { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Exception { get; init; }
}
