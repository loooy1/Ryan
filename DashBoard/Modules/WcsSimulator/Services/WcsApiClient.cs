using System.Net.Http.Json;
using System.Text.Json;
using Contracts.Dtos;
using Contracts.Entities;

namespace Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// WCSBackend（8230）管理面 API 客户端（Skill E：前端只读 API + 展示）。
/// BaseAddress 取 localStorage grcs_wcs_url（保留的 UI 偏好），缺省 http://localhost:8230。
/// </summary>
public class WcsApiClient
{
    private readonly HttpClient _http;
    private readonly LocalStoreService _store;
    private readonly BackendHealthService _health;
    private readonly ConnectionAlertService _alert;
    private bool _notifiedOffline; // WCS 后端已弹过离线告警
    private bool _notifiedGrcsOffline; // GRCS 后端已弹过离线告警

    /// <summary>抑制告警弹窗（自动轮询时设为 true，避免反复刷屏；手动操作保持 false 仍弹）。</summary>
    public bool SuppressAlerts { get; set; }
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, Converters = { new FlexibleDateTimeConverter() } };

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

    /// <summary>WCS 后端连接前置检查：同一次连续离线只弹一次，恢复后才允许再次提示。</summary>
    private bool ConnectionReady()
    {
        ResetOfflineAlertFlagsWhenRecovered();
        if (_health.WcsOnline == false)
        {
            ShowWcsOfflineAlertOnce();
            return false;
        }
        return true;
    }

    private void ResetOfflineAlertFlagsWhenRecovered()
    {
        if (_health.WcsOnline == true) _notifiedOffline = false;
        if (_health.GrcsOnline == true) _notifiedGrcsOffline = false;
    }

    private void ShowWcsOfflineAlertOnce()
    {
        if (SuppressAlerts || _notifiedOffline) return;
        _notifiedOffline = true;
        _alert.Show($"无法连接 WCS 后端（{BaseUrl}）\n请确认后端服务已启动，或检查「地址设置」中的连接地址。");
    }

    private void ShowGrcsOfflineAlertOnce(string hint)
    {
        if (SuppressAlerts || _notifiedGrcsOffline) return;
        _notifiedGrcsOffline = true;
        _alert.Show($"无法连接 GRCS 后端（{GrcsBaseUrl}）\n{hint}");
    }

    /// <summary>请求异常兜底：离线期间只弹一次，恢复在线后自动重置提示资格。</summary>
    private void NotifyIfUnreachable()
    {
        ResetOfflineAlertFlagsWhenRecovered();
        if (_health.WcsOnline != true) ShowWcsOfflineAlertOnce();
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

    /// <summary>同步库存账本（清空重建，以 GRCS 为准；有在途任务时后端返回 400 与拒绝原因）。</summary>
    public async Task<(bool ok, string json)> SyncInventoryAsync()
    {
        if (!GrcsReady()) return (false, "backend offline");
        try
        {
            var resp = await _http.PostAsJsonAsync(U("/api/wcs/inventory/sync"), new { });
            return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { NotifyIfUnreachable(); return (false, ex.Message); }
    }

    /// <summary>读取 WCS 本地储位快照，供手工入库地图渲染库存、锁和选点状态。</summary>
    public Task<List<WcsSlotRow>?> GetWcsSlotsAsync() => GetAsync<List<WcsSlotRow>>("/api/wcs/inventory/slots");

    /// <summary>Reads the WCS inventory-summary endpoint backed only by wcs_slots.</summary>
    public Task<InventorySummaryDto?> GetInventorySummaryAsync()
        => GetAsync<InventorySummaryDto>("/api/wcs/auto/inventory-summary");


    public async Task<(bool Ok, string Json)> SaveSortingAssociationAsync(SortingStationAssociationRequest request)
    {
        if (!ConnectionReady()) return (false, "backend offline");
        try
        {
            var response = await _http.PostAsJsonAsync(U("/api/wcs/inventory/sorting-association"), request);
            return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { NotifyIfUnreachable(); return (false, ex.Message); }
    }

    /// <summary>手工入库：后端先写 WCS，再逐条调用 RCS 入库接口。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> ManualInventoryEnterAsync(ManualInventoryEnterRequest request)
    {
        if (!ConnectionReady()) return (false, 0, "backend offline");
        try
        {
            var response = await _http.PostAsJsonAsync(U("/api/wcs/inventory/manual-enter"), request);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) { NotifyIfUnreachable(); return (false, 0, ex.Message); }
    }

    /// <summary>读取一次 RCS 在途托盘位置快照；页面不启动实时轮询。</summary>
    public Task<List<TransitPalletPositionDto>?> GetTransitPalletPositionsAsync()
        => GetAsync<List<TransitPalletPositionDto>>("/api/wcs/inventory/transit-pallet-positions");

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



    /// <summary>停止归巢（中断等待与后续下发，已下发的不撤销）。</summary>
    public async Task<bool> StopNestAsync()
    {
        return (await PostAsync<object, SaveResponse>("/api/wcs/auto/nest/stop", new { })) is { Success: true };
    }

    /// <summary>执行归巢（vehicles = 本次车队车名，可空 = 后端自动捕获当前就绪车）。</summary>
    public async Task<NestRunResult?> RunNestAsync(List<string>? vehicles = null)
    {
        if (!ConnectionReady()) return null;
        if (_health.GrcsOnline == false)
        {
            ShowGrcsOfflineAlertOnce("归巢需要 GRCS 车辆状态，请确认 GRCS 服务已启动。");
            return null;
        }
        return await PostAsync<object, NestRunResult>("/api/wcs/auto/nest/run", new { vehicles });
    }

    /// <summary>GRCS 全部车辆（归巢车辆多选用）。</summary>
    public async Task<List<VehicleInfoDto>> GetVehiclesAsync()
    {
        if (!ConnectionReady()) return [];
        if (_health.GrcsOnline == false)
        {
            ShowGrcsOfflineAlertOnce("车辆数据经 WCS 后端代理获取，请确认 GRCS 服务已启动。");
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

    /// <summary>库存分类汇总（纯空托/带货托/纯货物/锁定中，后端按 GRCS 库存统计）。</summary>
    // ── GRCS 代理接口（原 IWcsService/MockWcsService 并入，统一 HTTP 入口）──

    /// <summary>GRCS 连接守卫：WCS/GRCS 任一未连接即弹告警并返回失败（仅限经 WCS 代理请求 GRCS 数据的调用）。</summary>
    private bool GrcsReady()
    {
        if (!ConnectionReady()) return false;
        if (_health.GrcsOnline == false)
        {
            ShowGrcsOfflineAlertOnce("数据经 WCS 后端代理获取，请确认 GRCS 服务已启动。");
            return false;
        }
        return true;
    }

    /// <summary>代理结果兜底：GRCS 实际不可达（连接异常文本）时弹告警，覆盖健康探测尚未翻转的场景。</summary>
    private void NotifyGrcsIfUnreachable(string json)
    {
        if (_health.GrcsOnline != false
            && (json.Contains("积极拒绝") || json.Contains("无法连接") || json.Contains("超时")
                || json.Contains("Connection refused") || json.Contains("refused to connect")))
        {
            ShowGrcsOfflineAlertOnce("数据经 WCS 后端代理获取，请确认 GRCS 服务已启动。");
        }
    }

    /// <summary>发送车辆任务（代理 → GRCS /api/RawOrder/ChangeFloor）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> SendVehicleOrderAsync(
        string baseUrl, VehicleOrderRequest payload)
    {
        if (!GrcsReady()) return (false, 0, JsonSerializer.Serialize(new { error = "backend offline" }));
        var r = await PostProxyAsync("/api/wcs/grcs/change-floor", payload);
        if (!r.Ok) NotifyGrcsIfUnreachable(r.Json);
        return r;
    }

    /// <summary>任务组下发（代理 → GRCS /api/v1/task_receive，含三类模块后端执行）。</summary>
    public async Task<GrcsProxyResult?> SendTaskGroupAsync(WcsTaskGroup payload)
    {
        if (!GrcsReady()) return null;
        var r = await PostAsync<WcsTaskGroup, GrcsProxyResult>("/api/wcs/task/send", payload);
        if (r is { Ok: false }) NotifyGrcsIfUnreachable(r.Json ?? "");
        return r;
    }

    /// <summary>启动自动化模板执行（后端 AutoTemplateRunner 循环下发 GRCS 任务）。</summary>
    public async Task<StartResultDto?> StartTemplatesAsync(string tabId, List<string> templateIds)
    {
        if (!GrcsReady()) return null;
        var r = await PostAsync<object, StartResultDto>("/api/wcs/auto/start", new { tabId, templateIds });
        if (r is { Success: false } && r.Message?.Contains("GRCS") == true) NotifyGrcsIfUnreachable(r.Message);
        return r;
    }

    /// <summary>启动纯移动循环（后端 MoveLoopRunner 循环下发 MOVE_ONLY 任务到 GRCS）。</summary>
    public async Task<MoveLeaseResult?> StartMoveLoopAsync(string tabId, int interval, int priority, string orderIdPrefix)
    {
        if (!GrcsReady()) return null;
        var r = await PostAsync<object, MoveLeaseResult>("/api/wcs/auto/move/start", new
        {
            tabId,
            interval,
            priority,
            orderIdPrefix,
        });
        if (r is { Success: false } && r.Reason?.Contains("GRCS") == true) NotifyGrcsIfUnreachable(r.Reason);
        return r;
    }

    public class StartResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>查询容器库存（代理 → GRCS /api/Cargo，分页；场景按后端设置）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> QueryCargoInventoryAsync(
        string baseUrl, string? code = null, string? scene = null, string? locked = null,
        int pageNo = 1, int pageSize = 2000)
    {
        if (!GrcsReady()) return (false, 0, JsonSerializer.Serialize(new { error = "backend offline" }));
        var url = "/api/wcs/grcs/cargo" + $"?pageNo={pageNo}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(code)) url += $"&code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrWhiteSpace(locked)) url += $"&locked={Uri.EscapeDataString(locked)}";
        var r = await GetProxyAsync(url);
        if (!r.Ok) NotifyGrcsIfUnreachable(r.Json);
        return r;
    }

    /// <summary>读取 RCS 已配置的货物尺寸模型，供手工货物入库动态选择。</summary>
    public async Task<List<RcsCargoSizeDto>> GetCargoSizesAsync()
    {
        if (!GrcsReady()) return [];
        var response = await GetProxyAsync("/api/wcs/grcs/cargo-sizes");
        if (!response.Ok)
        {
            NotifyGrcsIfUnreachable(response.Json);
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array) return [];
            return JsonSerializer.Deserialize<List<RcsCargoSizeDto>>(records.GetRawText(), JsonOpts) ?? [];
        }
        catch { return []; }
    }

    /// <summary>模拟生成容器入库（代理 → GRCS /AutoContainerEnter，场景按后端设置）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> AutoContainerEnterAsync(string baseUrl, string sceneName,
        string prefix = "container", int num = -1, int floor = -1, int type = 1)
    {
        if (!GrcsReady()) return (false, 0, JsonSerializer.Serialize(new { error = "backend offline" }));
        var url = "/api/wcs/grcs/auto-container-enter"
            + $"?prefix={Uri.EscapeDataString(prefix)}&num={num}&floor={floor}&type={type}";
        var r = await GetProxyAsync(url);
        if (!r.Ok) NotifyGrcsIfUnreachable(r.Json);
        return r;
    }

    /// <summary>删除指定任务的所有阶段事件（WCS 管理接口 DELETE /api/wcs/task-stages/{taskId}）。</summary>
    /// <summary>Read task_records directly from the WCS backend database snapshot.</summary>
    public async Task<List<TaskRecord>?> GetTaskRecordsAsync()
    {
        if (!ConnectionReady()) return null;
        try { return await GetAsync<List<TaskRecord>>("/api/wcs/task-records"); }
        catch { return null; }
    }

    public async Task<(bool Ok, int StatusCode, string Json)> DeleteTaskStageAsync(string baseUrl, string taskId)
    {
        if (!ConnectionReady()) return (false, 0, JsonSerializer.Serialize(new { error = "backend offline" }));
        return await DeleteRawAsync("/api/wcs/task-stages/" + Uri.EscapeDataString(taskId));
    }

    /// <summary>调 GRCS 代理（GET）：后端返回 { ok, code, json }，解析为调用方 (Ok, StatusCode, Json)。</summary>
    private async Task<(bool Ok, int StatusCode, string Json)> GetProxyAsync(string url)
    {
        try
        {
            var resp = await _http.GetAsync(U(url));
            var body = await resp.Content.ReadAsStringAsync();
            return ParseProxy(body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>下载地图 zip（代理 → GRCS /api/Map/GetMap，成功返回 zip 字节流；失败返回错误文本并弹 GRCS 告警兜底）。</summary>
    public async Task<(bool Ok, string Error, byte[]? Bytes)> GetMapZipAsync()
    {
        if (!GrcsReady()) return (false, "backend offline", null);
        try
        {
            var resp = await _http.GetAsync(U("/api/wcs/grcs/map"));
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                var errText = await resp.Content.ReadAsStringAsync();
                var (ok, _, json) = ParseProxy(errText);
                var error = ok ? "后端代理返回异常" : json;
                NotifyGrcsIfUnreachable(error);
                return (false, error, null);
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return bytes.Length == 0 ? (false, "地图数据为空", null) : (true, "", bytes);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    private async Task<(bool Ok, int StatusCode, string Json)> PostProxyAsync<T>(string path, T payload)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(U(path), payload);
            var body = await resp.Content.ReadAsStringAsync();
            return ParseProxy(body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private async Task<(bool Ok, int StatusCode, string Json)> DeleteRawAsync(string path)
    {
        try
        {
            var resp = await _http.DeleteAsync(U(path));
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private static (bool Ok, int StatusCode, string Json) ParseProxy(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            int code = root.TryGetProperty("code", out var cEl) && cEl.ValueKind == JsonValueKind.Number ? cEl.GetInt32() : 0;
            string inner = root.TryGetProperty("json", out var jEl) ? jEl.GetString() ?? json : json;
            return (ok, code, inner);
        }
        catch { return (false, 0, json); }
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
