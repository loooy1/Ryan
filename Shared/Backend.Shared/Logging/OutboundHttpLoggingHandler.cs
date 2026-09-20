using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Backend.Shared.Logging;

public sealed class OutboundHttpLoggingHandler(ILogger<OutboundHttpLoggingHandler> logger) : DelegatingHandler
{
    private const int MaxBodyLength = 64 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var traceId = Activity.Current?.TraceId.ToString() ?? "";
        var requestBody = await ReadContentAsync(request.Content, cancellationToken);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["LogCategory"] = LogCategory.Http.ToString(),
            ["Component"] = "OutboundHttp",
            ["EventName"] = "HttpRequest",
            ["TraceId"] = traceId,
            ["Method"] = request.Method.Method,
            ["Url"] = request.RequestUri?.ToString() ?? ""
        });

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            stopwatch.Stop();
            // 成功的 GET 多为健康检查、页面或查询轮询；保留失败响应，避免 Http 日志刷屏。
            if (request.Method == HttpMethod.Get && response.IsSuccessStatusCode) return response;

            var responseBody = await ReadContentAsync(response.Content, cancellationToken);
            var level = (int)response.StatusCode >= 500 ? LogLevel.Error
                : (int)response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level,
                "HTTP {Method} {Url} -> {StatusCode} {DurationMs}ms Request={RequestBody} Response={ResponseBody}",
                request.Method.Method, request.RequestUri, (int)response.StatusCode, stopwatch.ElapsedMilliseconds,
                requestBody, responseBody);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "HTTP {Method} {Url} 失败 {DurationMs}ms Request={RequestBody}",
                request.Method.Method, request.RequestUri, stopwatch.ElapsedMilliseconds, requestBody);
            throw;
        }
    }

    private static async Task<string> ReadContentAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content == null) return "";
        var mediaType = content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return $"<binary contentType={mediaType} length={content.Headers.ContentLength?.ToString() ?? "unknown"}>";

        var body = await content.ReadAsStringAsync(cancellationToken);
        return SensitiveDataFilter.SanitizeAndLimit(body, MaxBodyLength);
    }
}
