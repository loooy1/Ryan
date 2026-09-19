using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Backend.Shared.Logging;

public sealed class CategorizedFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly FileLoggingOptions _options;
    private readonly string _rootDirectory;
    private readonly LogEventBuffer _buffer;
    private readonly Channel<AppLogEvent> _channel;
    private readonly Task _writerTask;
    private readonly HashSet<LogCategory> _enabled;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private int _disposed;

    public CategorizedFileLoggerProvider(FileLoggingOptions options, string contentRoot, LogEventBuffer buffer)
    {
        _options = options;
        _buffer = buffer;
        _rootDirectory = Path.IsPathRooted(options.RootDirectory)
            ? options.RootDirectory
            : Path.GetFullPath(Path.Combine(contentRoot, options.RootDirectory));
        _enabled = options.EnabledCategories
            .Select(x => Enum.TryParse<LogCategory>(x, true, out var value) ? value : LogCategory.System)
            .ToHashSet();
        _enabled.Add(LogCategory.System);

        foreach (var category in _enabled)
            Directory.CreateDirectory(CategoryDirectory(category));
        DeleteExpiredFiles();

        _channel = Channel.CreateUnbounded<AppLogEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(ProcessAsync);
    }

    public ILogger CreateLogger(string categoryName) => new CategorizedFileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    internal IDisposable? PushScope<TState>(TState state) where TState : notnull => _scopeProvider.Push(state);

    internal bool IsEnabled(LogLevel level) => level >= _options.MinimumLevel && level != LogLevel.None;

    internal void Write<TState>(string sourceContext, LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddProperties(properties, state);
        _scopeProvider.ForEachScope((scope, values) => AddProperties(values, scope), properties);

        var category = ResolveCategory(sourceContext, properties);
        properties.Remove("LogCategory");
        properties.Remove("{OriginalFormat}");
        properties.Remove("OriginalFormat");

        var entry = new AppLogEvent
        {
            Time = DateTimeOffset.Now,
            Level = level.ToString(),
            System = _options.SystemName,
            Category = category.ToString(),
            Component = Value(properties, "Component"),
            SourceContext = sourceContext,
            EventName = !string.IsNullOrWhiteSpace(eventId.Name)
                ? eventId.Name
                : Value(properties, "EventName", eventId.Id == 0 ? "" : eventId.Id.ToString()),
            TraceId = Value(properties, "TraceId"),
            TaskId = Value(properties, "TaskId"),
            RoundId = Value(properties, "RoundId"),
            ModuleId = Value(properties, "ModuleId"),
            StationCode = Value(properties, "StationCode"),
            ContainerCode = Value(properties, "ContainerCode"),
            CargoCode = Value(properties, "CargoCode"),
            Message = formatter(state, exception),
            Data = properties,
            Exception = exception?.ToString()
        };

        _buffer.Add(entry);
        _channel.Writer.TryWrite(entry);
    }

    private LogCategory ResolveCategory(string sourceContext, Dictionary<string, object?> properties)
    {
        if (properties.TryGetValue("LogCategory", out var explicitValue)
            && Enum.TryParse<LogCategory>(explicitValue?.ToString(), true, out var explicitCategory)
            && _enabled.Contains(explicitCategory))
            return explicitCategory;

        foreach (var route in _options.CategoryRoutes.OrderByDescending(x => x.Key.Length))
        {
            if (!sourceContext.StartsWith(route.Key, StringComparison.OrdinalIgnoreCase)) continue;
            if (Enum.TryParse<LogCategory>(route.Value, true, out var routed) && _enabled.Contains(routed))
                return routed;
        }
        return LogCategory.System;
    }

    private static void AddProperties<TState>(Dictionary<string, object?> target, TState state)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var pair in values) target[pair.Key] = Sanitize(pair.Key, pair.Value);
        }
    }

    private static object? Sanitize(string key, object? value)
    {
        if (key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authorization", StringComparison.OrdinalIgnoreCase))
            return "***";
        return value switch
        {
            null => null,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal or DateTime or DateTimeOffset or Guid => value,
            Enum enumValue => enumValue.ToString(),
            _ => value.ToString()
        };
    }

    private static string Value(Dictionary<string, object?> values, string key, string fallback = "")
        => values.TryGetValue(key, out var value) ? value?.ToString() ?? fallback : fallback;

    private async Task ProcessAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync())
        {
            try
            {
                var category = Enum.TryParse<LogCategory>(entry.Category, true, out var value) ? value : LogCategory.System;
                var path = ResolveFilePath(category, entry.Time.LocalDateTime);
                var json = JsonSerializer.Serialize(entry, _jsonOptions);
                await File.AppendAllTextAsync(path, json + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTime.Now:O}] 日志文件写入失败：{ex.Message}");
            }
        }
    }

    private string ResolveFilePath(LogCategory category, DateTime time)
    {
        var directory = CategoryDirectory(category);
        var prefix = category.ToString().ToLowerInvariant();
        var date = time.ToString("yyyyMMdd");
        var basePath = Path.Combine(directory, $"{prefix}-{date}.log");
        var limit = Math.Max(1, _options.FileSizeLimitMB) * 1024L * 1024L;
        if (!File.Exists(basePath) || new FileInfo(basePath).Length < limit) return basePath;

        for (var index = 1; ; index++)
        {
            var path = Path.Combine(directory, $"{prefix}-{date}_{index:000}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < limit) return path;
        }
    }

    private string CategoryDirectory(LogCategory category) => Path.Combine(_rootDirectory, category.ToString());

    private void DeleteExpiredFiles()
    {
        if (_options.RetainedDays <= 0) return;
        var cutoff = DateTime.Now.AddDays(-_options.RetainedDays);
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "*.log", SearchOption.AllDirectories))
        {
            try { if (File.GetLastWriteTime(path) < cutoff) File.Delete(path); }
            catch { /* 清理失败不阻止服务启动。 */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* 进程退出阶段不再抛出日志异常。 */ }
    }

    private sealed class CategorizedFileLogger(CategorizedFileLoggerProvider provider, string sourceContext) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider.PushScope(state);
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => provider.Write(sourceContext, logLevel, eventId, state, exception, formatter);
    }
}
