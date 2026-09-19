using Microsoft.Extensions.Logging;

namespace Backend.Shared.Logging;

public sealed class FileLoggingOptions
{
    public string SystemName { get; set; } = "Application";
    public string RootDirectory { get; set; } = "log";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public LogLevel ConsoleMinimumLevel { get; set; } = LogLevel.Error;
    public int RetainedDays { get; set; } = 30;
    public int FileSizeLimitMB { get; set; } = 100;
    public int RecentBufferSize { get; set; } = 2000;
    public string[] EnabledCategories { get; set; } = Enum.GetNames<LogCategory>();
    public Dictionary<string, string> CategoryRoutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
