using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Backend.Shared.Infrastructure;
using Contracts.Rcs.StationBusiness;
using Microsoft.EntityFrameworkCore;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Application.StationBusiness;

/// <summary>Persists station rules and serves immutable runtime snapshots to vehicle execution.</summary>
public sealed class RcsStationBusinessRuleService(IDbContextFactory<GrcsDbContext> factory,
    IHttpClientFactory httpClientFactory, ILogger<RcsStationBusinessRuleService> logger)
{
    private readonly ConcurrentDictionary<string, RcsStationBusinessRuleDto[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.Set<RcsStationBusinessRuleRow>().AsNoTracking().ToListAsync(token);
        _cache.Clear();
        foreach (var group in rows.GroupBy(x => Key(x.MapCode, x.PointCode)))
            _cache[group.Key] = group.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(ToDto).ToArray();
    }

    public IReadOnlyList<RcsStationBusinessRuleDto> Get(string mapCode, string? pointCode = null)
    {
        if (!string.IsNullOrWhiteSpace(pointCode))
            return _cache.TryGetValue(Key(mapCode, pointCode), out var rules) ? rules : [];
        return _cache.Values.SelectMany(x => x).Where(x => Eq(x.MapCode, mapCode))
            .OrderBy(x => x.PointCode).ThenBy(x => x.Event).ThenBy(x => x.SortOrder).ToArray();
    }

    public async Task<RcsStationBusinessRuleDto> SaveAsync(long id, SaveRcsStationBusinessRuleRequest request, CancellationToken token)
    {
        Validate(request);
        await using var db = await factory.CreateDbContextAsync(token);
        var row = id == 0 ? new RcsStationBusinessRuleRow() : await db.Set<RcsStationBusinessRuleRow>().FirstOrDefaultAsync(x => x.Id == id, token)
            ?? throw new KeyNotFoundException($"站点规则 {id} 不存在。");
        var oldKey = id == 0 ? null : Key(row.MapCode, row.PointCode);
        row.MapCode = request.MapCode.Trim(); row.PointCode = request.PointCode.Trim(); row.WaitPointCode = request.WaitPointCode.Trim(); row.Name = request.Name.Trim();
        row.Event = request.Event; row.ActionType = request.ActionType; row.ExecutionMode = request.ExecutionMode;
        row.HttpMethod = request.HttpMethod.ToUpperInvariant(); row.Url = request.Url.Trim();
        row.HeadersJson = request.HeadersJson; row.RequestBodyTemplate = request.RequestBodyTemplate;
        row.PermitResponsePath = request.PermitResponsePath.Trim(); row.DenyMessagePath = request.DenyMessagePath.Trim();
        row.TimeoutMs = Math.Clamp(request.TimeoutMs, 100, 120000); row.RetryCount = Math.Clamp(request.RetryCount, 0, 10);
        row.RetryDelayMs = Math.Clamp(request.RetryDelayMs, 0, 30000); row.SortOrder = request.SortOrder;
        row.IsEnabled = request.IsEnabled; row.UpdatedAt = DateTime.UtcNow;
        if (id == 0) { row.CreatedAt = row.UpdatedAt; db.Add(row); }
        await db.SaveChangesAsync(token);
        if (oldKey is not null && oldKey != Key(row.MapCode, row.PointCode))
        {
            var split = oldKey.Split('\0');
            var oldRows = await db.Set<RcsStationBusinessRuleRow>().AsNoTracking().Where(x => x.MapCode == split[0] && x.PointCode == split[1]).ToListAsync(token);
            _cache[oldKey] = oldRows.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(ToDto).ToArray();
        }
        var rows = await db.Set<RcsStationBusinessRuleRow>().AsNoTracking().Where(x => x.MapCode == row.MapCode && x.PointCode == row.PointCode).ToListAsync(token);
        _cache[Key(row.MapCode, row.PointCode)] = rows.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(ToDto).ToArray();
        return ToDto(row);
    }

    public async Task DeleteAsync(long id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.Set<RcsStationBusinessRuleRow>().FirstOrDefaultAsync(x => x.Id == id, token)
            ?? throw new KeyNotFoundException($"站点规则 {id} 不存在。");
        var key = Key(row.MapCode, row.PointCode); db.Remove(row); await db.SaveChangesAsync(token);
        var rows = await db.Set<RcsStationBusinessRuleRow>().AsNoTracking().Where(x => x.MapCode == row.MapCode && x.PointCode == row.PointCode).ToListAsync(token);
        _cache[key] = rows.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(ToDto).ToArray();
    }

    /// <returns>false when a WAIT_FOR_RESULT action rejects the event.</returns>
    public async Task<bool> ExecuteAsync(RcsStationRuleExecutionContext context, CancellationToken token)
    {
        if (!_cache.TryGetValue(Key(context.MapCode, context.PointCode), out var rules)) return true;
        foreach (var rule in rules.Where(x => x.IsEnabled && Eq(x.Event, context.Event) && string.IsNullOrWhiteSpace(x.WaitPointCode)))
        {
            if (rule.ExecutionMode == RcsStationRuleModes.FireAndForget)
            {
                _ = Task.Run(() => SendWithRetryAsync(rule, context, CancellationToken.None));
                continue;
            }
            var (allowed, message) = await SendWithRetryAsync(rule, context, token);
            if (!allowed) throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? $"站点 {context.PointCode} 的业务规则“{rule.Name}”未放行。" : message);
        }
        return true;
    }

    public async Task WaitForGateAsync(RcsStationRuleExecutionContext context, CancellationToken token)
    {
        if (!_cache.TryGetValue(Key(context.MapCode, context.PointCode), out var rules)) return;
        foreach (var rule in rules.Where(x => x.IsEnabled && x.Event == RcsStationEvents.BeforeEnter
            && Eq(x.WaitPointCode, context.WaitPointCode)))
        {
            var waitingLogged = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var (allowed, message) = await SendWithRetryAsync(rule, context, token);
                if (allowed)
                {
                    if (waitingLogged) logger.LogInformation("站点业务已放行 TaskId={TaskId} Vehicle={Vehicle} Target={Target} WaitPoint={WaitPoint} Rule={Rule}",
                        context.TaskId, context.VehicleId, context.PointCode, context.WaitPointCode, rule.Name);
                    break;
                }
                if (!waitingLogged)
                {
                    logger.LogInformation("车辆在等待站点等待业务放行 TaskId={TaskId} Vehicle={Vehicle} Target={Target} WaitPoint={WaitPoint} Rule={Rule} Reason={Reason}",
                        context.TaskId, context.VehicleId, context.PointCode, context.WaitPointCode, rule.Name, message ?? "尚未放行");
                    waitingLogged = true;
                }
                await Task.Delay(Math.Clamp(rule.RetryDelayMs, 250, 30000), token);
            }
        }
    }

    private async Task<(bool Allowed, string? Message)> SendWithRetryAsync(RcsStationBusinessRuleDto rule,
        RcsStationRuleExecutionContext context, CancellationToken token)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= rule.RetryCount; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(rule.HttpMethod), Expand(rule.Url, context));
                using var body = BuildBody(rule.RequestBodyTemplate, context);
                if (body is not null) request.Content = body;
                using (var headers = JsonDocument.Parse(rule.HeadersJson))
                    foreach (var header in headers.RootElement.EnumerateObject())
                    {
                        var value = Expand(header.Value.ToString(), context);
                        if (!request.Headers.TryAddWithoutValidation(header.Name, value))
                        { request.Content ??= new StringContent("", Encoding.UTF8, "application/json"); request.Content.Headers.TryAddWithoutValidation(header.Name, value); }
                    }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(rule.TimeoutMs));
                using var response = await httpClientFactory.CreateClient("station-business").SendAsync(request, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {text}");
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                var permit = ReadPath(json.RootElement, rule.PermitResponsePath);
                var allowed = permit.ValueKind == JsonValueKind.True || permit.ValueKind == JsonValueKind.String
                    && bool.TryParse(permit.GetString(), out var parsed) && parsed;
                var message = ReadPath(json.RootElement, rule.DenyMessagePath).ToString();
                if (allowed || rule.Event is not RcsStationEvents.BeforeEnter and not RcsStationEvents.BeforeAction and not RcsStationEvents.BeforeLeave)
                    return (true, null);
                return (false, message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                lastError = ex;
                if (attempt < rule.RetryCount) await Task.Delay(rule.RetryDelayMs, token);
            }
        }
        if (rule.ExecutionMode == RcsStationRuleModes.FireAndForget)
        { logger.LogWarning(lastError, "站点异步规则请求失败 Rule={RuleId} Event={Event} Point={Point}", rule.Id, rule.Event, context.PointCode); return (true, null); }
        return (false, $"站点业务请求失败：{lastError?.Message}");
    }

    private static HttpContent? BuildBody(string template, RcsStationRuleExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var expanded = template;
        foreach (var (key, value) in Tokens(context))
            expanded = expanded.Replace("{" + key + "}", JsonSerializer.Serialize(value).Trim('"'), StringComparison.OrdinalIgnoreCase);
        using var _ = JsonDocument.Parse(expanded);
        return new StringContent(expanded, Encoding.UTF8, "application/json");
    }
    private static string Expand(string value, RcsStationRuleExecutionContext c) => value
        .Replace("{TaskId}", c.TaskId, StringComparison.OrdinalIgnoreCase).Replace("{VehicleId}", c.VehicleId, StringComparison.OrdinalIgnoreCase)
        .Replace("{MapCode}", c.MapCode, StringComparison.OrdinalIgnoreCase).Replace("{Warehouse}", c.Warehouse, StringComparison.OrdinalIgnoreCase)
        .Replace("{PointCode}", c.PointCode, StringComparison.OrdinalIgnoreCase).Replace("{Event}", c.Event, StringComparison.OrdinalIgnoreCase)
        .Replace("{WaitPointCode}", c.WaitPointCode, StringComparison.OrdinalIgnoreCase)
        .Replace("{ContainerCode}", c.ContainerCode, StringComparison.OrdinalIgnoreCase).Replace("{Action}", c.Action, StringComparison.OrdinalIgnoreCase)
        .Replace("{Time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<KeyValuePair<string,string>> Tokens(RcsStationRuleExecutionContext c) => new Dictionary<string,string>
    { ["TaskId"]=c.TaskId, ["VehicleId"]=c.VehicleId, ["MapCode"]=c.MapCode, ["Warehouse"]=c.Warehouse,
      ["PointCode"]=c.PointCode, ["Event"]=c.Event, ["ContainerCode"]=c.ContainerCode, ["Action"]=c.Action,
      ["WaitPointCode"]=c.WaitPointCode, ["Time"]=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
    private static JsonElement ReadPath(JsonElement root, string path)
    { var cur = root; foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(segment, out var next)) cur = next; else return default; return cur; }
    private static void Validate(SaveRcsStationBusinessRuleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.MapCode) || string.IsNullOrWhiteSpace(r.PointCode) || string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("地图、站点和规则名称不能为空。");
        if (!RcsStationEvents.All.Contains(r.Event, StringComparer.Ordinal)) throw new ArgumentException("不支持的站点触发事件。");
        if (r.Event == RcsStationEvents.BeforeEnter && string.IsNullOrWhiteSpace(r.WaitPointCode)) throw new ArgumentException("进入站点前规则必须选择一个等待站点。");
        if (r.Event == RcsStationEvents.BeforeEnter && Eq(r.PointCode, r.WaitPointCode)) throw new ArgumentException("业务站点和等待站点不能是同一个站点。");
        if (r.ActionType != "HTTP") throw new ArgumentException("当前版本只支持 HTTP 动作。");
        if (r.ExecutionMode is not RcsStationRuleModes.WaitForResult and not RcsStationRuleModes.FireAndForget) throw new ArgumentException("不支持的执行模式。");
        if (r.Event is RcsStationEvents.BeforeEnter or RcsStationEvents.BeforeAction or RcsStationEvents.BeforeLeave && r.ExecutionMode != RcsStationRuleModes.WaitForResult) throw new ArgumentException("进入、动作和离开前事件必须等待业务结果。");
        if (r.Event is RcsStationEvents.AfterEnter or RcsStationEvents.AfterAction or RcsStationEvents.AfterLeave && r.ExecutionMode != RcsStationRuleModes.FireAndForget) throw new ArgumentException("到达后、动作后和离开后事件必须采用异步通知。");
        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("HTTP 地址必须是完整的 http/https URL。");
        if (r.HttpMethod.ToUpperInvariant() is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) throw new ArgumentException("不支持的 HTTP 方法。");
        using var headers = JsonDocument.Parse(r.HeadersJson);
        if (headers.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("请求头必须是 JSON 对象。");
        using var __ = JsonDocument.Parse(r.RequestBodyTemplate);
    }
    private static RcsStationBusinessRuleDto ToDto(RcsStationBusinessRuleRow x) => new()
    { Id=x.Id, MapCode=x.MapCode, PointCode=x.PointCode, WaitPointCode=x.WaitPointCode, Name=x.Name, Event=x.Event, ActionType=x.ActionType, ExecutionMode=x.ExecutionMode,
      HttpMethod=x.HttpMethod, Url=x.Url, HeadersJson=x.HeadersJson, RequestBodyTemplate=x.RequestBodyTemplate,
      PermitResponsePath=x.PermitResponsePath, DenyMessagePath=x.DenyMessagePath, TimeoutMs=x.TimeoutMs, RetryCount=x.RetryCount,
      RetryDelayMs=x.RetryDelayMs, SortOrder=x.SortOrder, IsEnabled=x.IsEnabled, UpdatedAt=x.UpdatedAt.ToString("O") };
    private static string Key(string map, string point) => $"{map.Trim()}\0{point.Trim()}";
    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
