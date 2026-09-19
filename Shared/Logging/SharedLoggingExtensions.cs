using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Backend.Shared.Logging;

public static class SharedLoggingExtensions
{
    private const int MaxHttpBodyLength = 64 * 1024;
    public static IServiceCollection AddSharedHttpClientLogging(this IServiceCollection services)
    {
        services.AddTransient<OutboundHttpLoggingHandler>();
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(client => client.AddHttpMessageHandler<OutboundHttpLoggingHandler>());
        return services;
    }

    public static WebApplicationBuilder AddSharedLogging(this WebApplicationBuilder builder)
    {
        var options = new FileLoggingOptions();
        builder.Configuration.GetSection("FileLogging").Bind(options);
        var buffer = new LogEventBuffer(options.RecentBufferSize);
        var provider = new CategorizedFileLoggerProvider(options, builder.Environment.ContentRootPath, buffer);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(buffer);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddFilter<ConsoleLoggerProvider>(null, options.ConsoleMinimumLevel);
        builder.Logging.AddProvider(provider);
        builder.Logging.AddFilter<CategorizedFileLoggerProvider>(null, options.MinimumLevel);
        return builder;
    }

    public static IApplicationBuilder UseSharedHttpLogging(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            var requestBody = await ReadRequestBodyAsync(context.Request);
            var scopeData = new Dictionary<string, object?>
            {
                ["LogCategory"] = LogCategory.Http.ToString(),
                ["Component"] = "InboundHttp",
                ["EventName"] = "HttpRequest",
                ["TraceId"] = traceId,
                ["Method"] = context.Request.Method,
                ["Path"] = context.Request.Path.Value ?? ""
            };
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Http.Inbound");
            using var scope = logger.BeginScope(scopeData);
            var originalBody = context.Response.Body;
            var captureBody = !context.WebSockets.IsWebSocketRequest
                && !context.Request.Path.StartsWithSegments("/hubs");
            await using var capture = captureBody ? new LimitedCaptureStream(originalBody, MaxHttpBodyLength) : null;
            if (capture != null) context.Response.Body = capture;
            try { await next(); }
            finally
            {
                if (capture != null) context.Response.Body = originalBody;
                stopwatch.Stop();
            }

            if (context.Request.Path.StartsWithSegments("/health") && context.Response.StatusCode < 500) return;
            var level = context.Response.StatusCode >= 500 ? LogLevel.Error
                : context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
            var responseBody = capture == null ? "<stream-or-websocket>" : DescribeCapturedBody(context.Response.ContentType, capture);
            logger.Log(level,
                "{Method} {Path} -> {StatusCode} {DurationMs}ms Request={RequestBody} Response={ResponseBody}",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode, stopwatch.ElapsedMilliseconds,
                requestBody, responseBody);
        });

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0) return "";
        var contentType = request.ContentType ?? "";
        if (!IsTextContent(contentType))
            return $"<binary contentType={contentType} length={request.ContentLength}>";

        request.EnableBuffering();
        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var buffer = new char[MaxHttpBodyLength + 1];
        var count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        request.Body.Position = 0;
        var body = new string(buffer, 0, Math.Min(count, MaxHttpBodyLength));
        if (count > MaxHttpBodyLength) body += $"<truncated total>{MaxHttpBodyLength}>";
        return SensitiveDataFilter.SanitizeAndLimit(body, MaxHttpBodyLength);
    }

    private static string DescribeCapturedBody(string? contentType, LimitedCaptureStream capture)
    {
        if (!IsTextContent(contentType ?? ""))
            return $"<binary contentType={contentType} captured={capture.CapturedLength}>";
        return SensitiveDataFilter.SanitizeAndLimit(capture.GetText(), MaxHttpBodyLength);
    }

    private static bool IsTextContent(string contentType)
        => contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
           || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private sealed class LimitedCaptureStream(Stream inner, int limit) : Stream
    {
        private readonly MemoryStream _capture = new(Math.Min(limit, 4096));
        public int CapturedLength => (int)_capture.Length;
        public string GetText() => System.Text.Encoding.UTF8.GetString(_capture.ToArray());
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            Capture(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Capture(buffer.AsSpan(offset, count));
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }
        private void Capture(ReadOnlySpan<byte> value)
        {
            var remaining = limit - (int)_capture.Length;
            if (remaining > 0) _capture.Write(value[..Math.Min(value.Length, remaining)]);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _capture.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _capture.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
