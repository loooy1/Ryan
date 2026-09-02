using System.Net.Http.Json;
using System.Text.Json;
using GrcsBackend.Contracts.Dtos;

namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// WCS 服务 HTTP 实现（GRCS 对接全部经 GrcsBackend 代理，前端不再直连 GRCS 8224）。
/// baseUrl 派生路径：GRCS 类接口走 /api/wcs/grcs/*（GRCS 地址/场景名由后端设置统一持有，
/// 入参 baseUrl/apiVersion 仅保留签名兼容、实际不使用）；WCS 后端管理接口走 /api/wcs/*。
/// 本类 base 取 localStorage grcs_wcs_url（地图信息页配置保存）。
/// </summary>
public class MockWcsService : IWcsService
{
    private readonly HttpClient _http;
    private readonly LocalStoreService _store;

    public MockWcsService(HttpClient http, LocalStoreService store)
    {
        _http = http;
        _store = store;
    }

    /// <summary>WCS 后端地址（grcs_wcs_url，缺省 localhost:8230）。</summary>
    private string WcsBase
    {
        get
        {
            var url = _store["grcs_wcs_url"];
            return string.IsNullOrEmpty(url) || url == "null" ? "http://localhost:8230" : url;
        }
    }

    private string U(string path) => WcsBase.TrimEnd('/') + path;

    // ── 车辆任务（代理 → GRCS /api/RawOrder/ChangeFloor）──

    public async Task<(bool Ok, int StatusCode, string Json)> SendVehicleOrderAsync(
        string baseUrl, VehicleOrderRequest payload)
        => await ProxyPostAsync("/api/wcs/grcs/change-floor", payload);

    // ── 库存查询（代理 → GRCS /api/Cargo）──

    public async Task<(bool Ok, int StatusCode, string Json)> QueryCargoInventoryAsync(
        string baseUrl, string? code = null, string? scene = null, string? locked = null,
        int pageNo = 1, int pageSize = 2000)
    {
        var url = U("/api/wcs/grcs/cargo") + $"?pageNo={pageNo}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(code)) url += $"&code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrWhiteSpace(locked)) url += $"&locked={Uri.EscapeDataString(locked)}";
        try
        {
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return ParseProxy(body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    /// <summary>模拟生成容器入库（代理 → GRCS /AutoContainerEnter，场景按后端设置）。</summary>
    public async Task<(bool Ok, int StatusCode, string Json)> AutoContainerEnterAsync(string baseUrl, string sceneName,
        string prefix = "container", int num = -1, int floor = -1, int type = 1)
    {
        var url = U("/api/wcs/grcs/auto-container-enter")
            + $"?prefix={Uri.EscapeDataString(prefix)}&num={num}&floor={floor}&type={type}";
        try
        {
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return ParseProxy(body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    // ── 任务阶段（WCS 后端管理接口 /api/wcs）──

    public async Task<(bool Ok, int StatusCode, string Json)> DeleteTaskStageAsync(string baseUrl, string taskId)
        => await DeleteRawAsync("/api/wcs/task-stages/" + Uri.EscapeDataString(taskId));

    // ── 接驳位审批（WCS 后端管理接口 /api/wcs）──

// ── 通用方法 ──

    /// <summary>调 GRCS 代理：后端返回 { ok, code, json }，解析为调用方 (Ok, StatusCode, Json)。</summary>
    private async Task<(bool Ok, int StatusCode, string Json)> ProxyPostAsync<T>(string path, T payload)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(U(path), payload);
            var body = await resp.Content.ReadAsStringAsync();
            return ParseProxy(body);
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

    private async Task<(bool Ok, int StatusCode, string Json)> GetRawAsync(string path)
    {
        try
        {
            var resp = await _http.GetAsync(U(path));
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception ex) { return (false, 0, JsonSerializer.Serialize(new { error = ex.Message })); }
    }

    private async Task<(bool Ok, int StatusCode, string Json)> PostRawAsync<T>(string path, T payload)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(U(path), payload);
            var body = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
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
}