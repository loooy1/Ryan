namespace Contracts.Dtos;

public class ModuleExecLogEntry
{
    public long Id { get; set; }
    public string Time { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string Point { get; set; } = "";
    public string Module { get; set; } = "";
    public string ExecutionPhase { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Ok { get; set; }
    public int HttpCode { get; set; }
    public string Detail { get; set; } = "";
    public string DetailJson { get; set; } = "{}";
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";

    public static string ToPhaseLabel(string phase) => phase?.Trim().ToUpperInvariant() switch
    {
        "BEFORE_MODULE" => "起点之前",
        "AFTER_START_MODULE" => "起点之后",
        "AFTER_END_MODULE" => "终点之后",
        _ => phase ?? ""
    };

    public static string BuildSummary(string? detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson)) return "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailJson);
            if (document.RootElement.TryGetProperty("summary", out var summary))
                return summary.GetString() ?? "";
            if (document.RootElement.TryGetProperty("response", out var response)
                && response.TryGetProperty("exception", out var exception))
                return exception.GetString() ?? "";
        }
        catch { }
        return "";
    }
}
