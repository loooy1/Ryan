using System.Net.Http.Json;
using System.Text.Json;
using GrcsBackend.Contracts.Dtos;

namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// GrcsBackend（8230）管理面 API 客户端（Skill E：前端只读 API + 展示）。
/// BaseAddress 取 localStorage grcs_wcs_url（保留的 UI 偏好），缺省 http://localhost:8230。
/// </summary>
public class WcsApiClient
{
    private readonly HttpClient _http;
    private readonly LocalStoreService _store;
    private readonly BackendHealthService _health;
    private readonly ConnectionAlertService _alert;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, Converters = { new FlexibleDateTimeConverter() } };

    /// <summary>静默模式（AutomationHub 常驻轮询置 true）：请求不弹连接告警，避免轮询刷屏。</summary>
    public bool SuppressConnectionAlert { get; set; }

    /// <summary>后端时间常为 "yyyy-MM-dd HH:mm:ss.fff"（空格）格式，System.Text.Json 默认仅认 ISO 8601；
    /// 这里做容错，避免单个时间字段解析失败导致整条列表反序列化抛异常返回 null。</summary>
    private class FlexibleDateTimeConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
    {
        public override DateTime Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            var s = reader.GetString();
            return DateTime.TryParse(s, out var dt) ? dt : default;
        }
        public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTime value, System.Text.Json.JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    public WcsApiClient(HttpClient http, LocalStoreService store, BackendHealthService health, ConnectionAlertService alert)
    {
        _http = http;
        _store = store;
        _health = health;
        _alert = alert;
    }

    /// <summary>WCS 后端连接前置检查：未连接时弹告警并返回 false（调用方短路返回失败值）。</summary>
    private bool ConnectionReady()
    {
        if (SuppressConnectionAlert) return true;
        if (_health.WcsOnline == false)
        {
            _alert.Show($"无法连接 WCS 后端（{BaseUrl}）\n请确认后端服务已启动，或检查「地址设置」中的连接地址。");
            return false;
        }
        return true;
    }

    /// <summary>请求异常兜底：实际请求失败且后端状态非在线时弹告警（覆盖健康探测尚未翻转的场景）。</summary>
    private void NotifyIfUnreachable()
    {
        if (SuppressConnectionAlert) return;
        if (_health.WcsOnline != true)
            _alert.Show($"无法连接 WCS 后端（{BaseUrl}）\n请确认后端服务已启动，或检查「地址设置」中的连接地址。");
    }

    public string BaseUrl
    {
        get
        {
            var url = _store["grcs_wcs_url"];
            return string.IsNullOrEmpty(url) || url == "null" ? "http://localhost:8230" : url;
        }
    }

    /// <summary>GRCS 后端地址（grcs_grcs_url，地图信息页地址设置保存），缺省 http://localhost:8224。</summary>
    public string GrcsBaseUrl
    {
        get
        {
            var url = _store["grcs_grcs_url"];
            return string.IsNullOrEmpty(url) || url == "null" ? "http://localhost:8224" : url;
        }
    }

    private string U(string path) => BaseUrl.TrimEnd('/') + path;

    public async Task<T?> GetAsync<T>(string path)
    {
        if (!ConnectionReady()) return default;
        try
        {
            var json = await _http.GetStringAsync(U(path));
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            // 仅当 T 为集合类型时，从 {success,items/data/result/list:[...]} 包装体提取数组再反序列化；
            // 包装类型（MockRuleListResponse 等）按整体对象反序列化，避免误把数组当包装对象解析。
            if (el.ValueKind == JsonValueKind.Object
                && typeof(T) != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(typeof(T)))
            {
                foreach (var key in new[] { "items", "data", "result", "list" })
                {
                    if (el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array)
                        return JsonSerializer.Deserialize<T>(p.GetRawText(), JsonOpts);
                }
            }
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch { NotifyIfUnreachable(); return default; }
    } 

    public async Task<T?> PostAsync<TReq, T>(string path, TReq body)
    {
        if (!ConnectionReady()) return default;
        try
        {
            var resp = await _http.PostAsJsonAsync(U(path), body);
            var json = await resp.Content.ReadAsStringAsync();
            return resp.IsSuccessStatusCode ? JsonSerializer.Deserialize<T>(json, JsonOpts) : default;
        }
        catch { NotifyIfUnreachable(); return default; }
    }

    public async Task<string> PostAsync<TReq>(string path, TReq body)
    {
        if (!ConnectionReady()) return JsonSerializer.Serialize(new { error = "backend offline" });
        try
        {
            var resp = await _http.PostAsJsonAsync(U(path), body);
            var json = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return json;
            return JsonSerializer.Serialize(new { error = $"HTTP {(int)resp.StatusCode}" });
        }
        catch (Exception ex) { NotifyIfUnreachable(); return JsonSerializer.Serialize(new { error = ex.Message }); }
    }

    public async Task<T?> PutAsync<TReq, T>(string path, TReq body)
    {
        if (!ConnectionReady()) return default;
        try
        {
            var resp = await _http.PutAsJsonAsync(U(path), body);
            var json = await resp.Content.ReadAsStringAsync();
            return resp.IsSuccessStatusCode ? JsonSerializer.Deserialize<T>(json, JsonOpts) : default;
        }
        catch { NotifyIfUnreachable(); return default; }
    }

    /// <summary>带状态码的 PUT：无论成功失败都返回响应体（用于保存校验失败时的错误透传）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> PutWithStatusAsync<TReq>(string path, TReq body)
    {
        if (!ConnectionReady()) return (false, 0, "backend offline");
        try
        {
            var resp = await _http.PutAsJsonAsync(U(path), body);
            var json = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, json);
        }
        catch (Exception ex) { NotifyIfUnreachable(); return (false, 0, ex.Message); }
    }

    public async Task<bool> DeleteAsync(string path)
    {
        if (!ConnectionReady()) return false;
        try { return (await _http.DeleteAsync(U(path))).IsSuccessStatusCode; }
        catch { NotifyIfUnreachable(); return false; }
    }

    // ── 任务类型模板 / 功能模板（后端 SQLite 持久化，跨浏览器共享）──

    /// <summary>拉取全部任务类型模板（后端 task_templates 表）。返回 null 表示后端不可用。</summary>
    public async Task<List<TaskTemplateDto>?> GetTaskTemplatesAsync()
    {
        var resp = await GetAsync<TemplateListResponse>( "/api/wcs/templates");
        return resp?.Items;
    }

    /// <summary>整体保存任务类型模板（替换后端全部）。</summary>
    public async Task<bool> SaveTaskTemplatesAsync(IEnumerable<TaskTemplateDto> items)
    {
        return await PostAsync<IEnumerable<TaskTemplateDto>, SaveResponse>("/api/wcs/templates", items) is { Success: true };
    }

    /// <summary>按 Value 删除一条任务类型模板。</summary>
    public async Task<bool> DeleteTaskTemplateAsync(string value)
    {
        return await DeleteAsync($"/api/wcs/templates/{Uri.EscapeDataString(value)}");
    }

    /// <summary>拉取全部功能模板（后端 feature_modules 表）。返回 null 表示后端不可用。</summary>
    public async Task<List<FeatureModuleDto>?> GetFeatureModulesAsync()
    {
        var resp = await GetAsync<ModuleListResponse>("/api/wcs/modules");
        return resp?.Items;
    }

    /// <summary>整体保存功能模板（替换后端全部）。</summary>
    public async Task<bool> SaveFeatureModulesAsync(IEnumerable<FeatureModuleDto> items)
    {
        return await PostAsync<IEnumerable<FeatureModuleDto>, SaveResponse>("/api/wcs/modules", items) is { Success: true };
    }

    /// <summary>按 Id 删除一条功能模板。</summary>
    public async Task<bool> DeleteFeatureModuleAsync(string id)
    {
        return await DeleteAsync($"/api/wcs/modules/{Uri.EscapeDataString(id)}");
    }

    /// <summary>清空模块执行记录（后端内存环形缓冲）。</summary>
    public async Task<bool> ClearModuleExecLogsAsync()
    {
        return await DeleteAsync("/api/wcs/modules/logs");
    }

    /// <summary>重试单条模块执行记录（后端恢复任务上下文重新 POST，MsgTime 用当前时间；新记录经 SignalR 推送）。</summary>
    public async Task<bool> RetryModuleLogAsync(long id)
    {
        return (await PostAsync<object, SaveResponse>($"/api/wcs/modules/logs/{id}/retry", new { })) is { Success: true };
    }

    /// <summary>停止归巢（中断等待与后续下发，已下发的不撤销）。</summary>
    public async Task<bool> StopNestAsync()
    {
        return (await PostAsync<object, SaveResponse>("/api/wcs/auto/nest/stop", new { })) is { Success: true };
    }

    /// <summary>执行归巢（vehicles = 本次车队车名，可空 = 后端自动捕获当前就绪车）。</summary>
    public async Task<NestRunResult?> RunNestAsync(List<string>? vehicles = null)
    {
        if (!ConnectionReady()) return null;
        if (!SuppressConnectionAlert && _health.GrcsOnline == false)
        {
            _alert.Show($"无法连接 GRCS 后端（{GrcsBaseUrl}）\n归巢需要 GRCS 车辆状态，请确认 GRCS 服务已启动。");
            return null;
        }
        return await PostAsync<object, NestRunResult>("/api/wcs/auto/nest/run", new { vehicles });
    }

    /// <summary>GRCS 全部车辆（归巢车辆多选用）。</summary>
    public async Task<List<VehicleInfoDto>> GetVehiclesAsync()
    {
        if (!ConnectionReady()) return [];
        if (!SuppressConnectionAlert && _health.GrcsOnline == false)
        {
            _alert.Show($"无法连接 GRCS 后端（{GrcsBaseUrl}）\n车辆数据经 WCS 后端代理获取，请确认 GRCS 服务已启动。");
            return [];
        }
        try
        {
            var json = await GetAsync<JsonElement>("/api/wcs/auto/vehicles");
            if (json.TryGetProperty("vehicles", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<VehicleInfoDto>>(arr.GetRawText(), JsonOpts) ?? [];
            return [];
        }
        catch { return []; }
    }

    /// <summary>GRCS 接口说明清单（后端硬编码接口记录）。</summary>
    public async Task<List<GrcsApiDocDto>> GetGrcsApiDocsAsync()
    {
        try { return await GetAsync<List<GrcsApiDocDto>>("/api/wcs/grcs-api-docs") ?? []; }
        catch { return []; }
    }

    // ── 通用 Mock 规则（入站可配）──
    public async Task<List<MockRuleDto>?> GetMockRulesAsync()
    {
        var resp = await GetAsync<MockRuleListResponse>("/api/wcs/mocks");
        return resp?.Items;
    }
    public async Task<bool> SaveMockRulesAsync(IEnumerable<MockRuleDto> items)
    {
        return await PostAsync<IEnumerable<MockRuleDto>, SaveResponse>("/api/wcs/mocks", items) is { Success: true };
    }
    private class MockRuleListResponse { public bool Success { get; set; } public List<MockRuleDto>? Items { get; set; } }

    // ── 准入请求（RCS->WCS station_entry_request）──

    public async Task<bool> DecideMockAsync(string key, bool allow) => (await PostAsync<object, object>($"/api/wcs/mock-approvals/decisions/{Uri.EscapeDataString(key)}", new { allow })) != null;
    public async Task<bool> DeleteMockApprovalAsync(string key) => await DeleteAsync($"/api/wcs/mock-approvals/{Uri.EscapeDataString(key)}");
    public async Task<bool> ClearMockApprovalsAsync() => await DeleteAsync("/api/wcs/mock-approvals");

    private class TemplateListResponse
    {
        public bool Success { get; set; }
        public List<TaskTemplateDto>? Items { get; set; }
    }

    private class ModuleListResponse
    {
        public bool Success { get; set; }
        public List<FeatureModuleDto>? Items { get; set; }
    }

    private class SaveResponse
    {
        public bool Success { get; set; }
    }
}
