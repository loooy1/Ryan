using System.Net.Http.Json;
using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Map;
using Contracts.Rcs.Inventory;
using Contracts.Rcs.Route;
using Contracts.Rcs.Tasks;
using Contracts.Rcs.Vehicle;

namespace Dashboard.Modules.RcsSimulator.Services;

public sealed class RcsApiClient
{
    private string _baseUrl = "http://localhost:8232";
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    // 地图包含大量点、路径及原始 GRCS 元数据；保存后后端还会重载算法地图缓存。
    private readonly HttpClient _mapHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public string BaseUrl => _baseUrl;

    public void SetBaseUrl(string url)
    {
        var normalized = (url ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("RCS 地址必须是 http:// 或 https:// 开头的完整地址。", nameof(url));
        _baseUrl = uri.ToString().TrimEnd('/');
    }

    public async Task<bool> CheckOnlineAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            // 后端与数据库就绪即可在线，地图是否存在由地图页面单独处理。
            using var response = await _http.GetAsync(U("/RCS_ready"), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public Task<RcsMapSnapshot?> GetMapAsync(CancellationToken token = default) =>
        _http.GetFromJsonAsync<RcsMapSnapshot>(U("/api/rcs/map"), token);

    public Task<RcsMapSnapshot?> ReloadMapAsync(CancellationToken token = default) =>
        PostAsync<RcsMapSnapshot>("/api/rcs/map/reload", new { }, token);

    public async Task<AlgorithmSettingsDto> GetAlgorithmSettingsAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync(U("/api/rcs/settings/algorithm"), token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<AlgorithmSettingsDto>(cancellationToken: token)
            ?? throw new InvalidOperationException("RCS 未返回有效的算法设置。");
    }

    public async Task<AlgorithmSettingsDto> SaveAlgorithmSettingsAsync(AlgorithmSettingsDto settings, CancellationToken token = default)
    {
        using var response = await _http.PutAsJsonAsync(U("/api/rcs/settings/algorithm"), settings, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<AlgorithmSettingsDto>(cancellationToken: token)
            ?? throw new InvalidOperationException("RCS 未确认算法设置保存成功。");
    }

    public Task<RcsMapEditorDto?> GetEditorMapAsync(string mapCode = "default", CancellationToken token = default) =>
        _mapHttp.GetFromJsonAsync<RcsMapEditorDto>(U($"/api/rcs/editor/map?mapCode={Uri.EscapeDataString(mapCode)}"), token);

    public async Task SaveEditorMapAsync(RcsMapEditorDto map, CancellationToken token = default)
    {
        using var response = await _mapHttp.PutAsJsonAsync(U("/api/rcs/editor/map"), map, token);
        await EnsureSuccessAsync(response, token);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
        if (result.ValueKind != System.Text.Json.JsonValueKind.Object
            || !result.TryGetProperty("success", out var success)
            || success.ValueKind != System.Text.Json.JsonValueKind.True
            || !result.TryGetProperty("mapCode", out var savedCode)
            || savedCode.ValueKind != System.Text.Json.JsonValueKind.String
            || savedCode.GetString() != map.MapCode)
        {
            var message = result.ValueKind == System.Text.Json.JsonValueKind.Object
                && result.TryGetProperty("message", out var detail)
                && detail.ValueKind == System.Text.Json.JsonValueKind.String
                ? detail.GetString() : "目标服务未确认地图保存成功，请检查 RCS 后端地址。";
            throw new InvalidOperationException(message);
        }
    }

    public async Task<IReadOnlyList<VehicleStateDto>> GetVehiclesAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync(U("/api/rcs/vehicles"), token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<VehicleStateDto[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("RCS 未返回有效车辆列表。");
    }
    public async Task<IReadOnlyList<VehicleProtocolInfoDto>> GetVehicleProtocolsAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync(U("/api/rcs/vehicles/protocols"), token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<VehicleProtocolInfoDto[]>(cancellationToken: token) ?? [];
    }
    public async Task<IReadOnlyList<RcsTaskDto>> GetTasksAsync(int limit = 500, CancellationToken token = default)
    {
        using var response = await _http.GetAsync(U($"/api/rcs/tasks?limit={Math.Clamp(limit, 1, 500)}"), token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<RcsTaskDto[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("RCS 未返回有效任务列表。");
    }
    public Task<VehicleStateDto?> AddVehicleAsync(CreateVehicleRequest request, CancellationToken token = default) =>
        PostAsync<VehicleStateDto>("/api/rcs/vehicles", request, token);
    public async Task<VehicleStateDto?> UpdateVehicleAsync(string id, UpdateVehicleRequest request, CancellationToken token = default)
    {
        using var response = await _http.PutAsJsonAsync(U(VehiclePath(id)), request, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<VehicleStateDto>(cancellationToken: token);
    }
    public async Task DeleteVehicleAsync(string id, CancellationToken token = default)
    {
        using var response = await _http.DeleteAsync(U(VehiclePath(id)), token);
        await EnsureSuccessAsync(response, token);
    }
    public Task<RouteDto?> RunVehicleAsync(string id, RunVehicleRequest request, CancellationToken token = default) =>
        PostAsync<RouteDto>(VehiclePath(id) + "/run", request, token);
    public Task<RcsTaskReceiveResponse?> SubmitManualTaskAsync(RcsTaskReceiveRequest request, CancellationToken token = default) =>
        PostAsync<RcsTaskReceiveResponse>("/api/rcs/tasks/manual", request, token);
    public Task PauseVehicleAsync(string id, CancellationToken token = default) => ControlVehicleAsync(id, "pause", token);
    public Task ResumeVehicleAsync(string id, CancellationToken token = default) => ControlVehicleAsync(id, "resume", token);
    public Task StopVehicleAsync(string id, CancellationToken token = default) => ControlVehicleAsync(id, "stop", token);
    public async Task ResetVehicleAsync(string id, string pointCode, CancellationToken token = default)
    {
        using var response = await _http.PostAsJsonAsync(U(VehiclePath(id) + "/reset"), pointCode, token);
        await EnsureSuccessAsync(response, token);
    }
    public async Task<VehicleStateDto?> SetVehiclePositionAsync(string id, string pointCode, CancellationToken token = default)
    {
        using var response = await _http.PutAsJsonAsync(U(VehiclePath(id) + "/position"), pointCode, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<VehicleStateDto>(cancellationToken: token);
    }
    private async Task ControlVehicleAsync(string id, string operation, CancellationToken token)
    {
        using var response = await _http.PostAsync(U(VehiclePath(id) + "/" + operation), null, token);
        await EnsureSuccessAsync(response, token);
    }
    private static string VehiclePath(string id) => "/api/rcs/vehicles/" + Uri.EscapeDataString(id);
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(token);
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            foreach (var key in new[] { "message", "error", "detail" })
                if (json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    throw new InvalidOperationException(value.GetString());
        }
        catch (System.Text.Json.JsonException) { }
        throw new HttpRequestException($"RCS 请求失败：HTTP {(int)response.StatusCode}", null, response.StatusCode);
    }
    public Task<RouteDto?> PreviewAsync(RunVehicleRequest request, CancellationToken token = default) =>
        PostAsync<RouteDto>("/api/rcs/routes/preview", request, token);

    public async Task<IReadOnlyList<RcsInventoryModelDto>> GetInventoryModelsAsync(string? itemType = null, CancellationToken token = default)
    {
        var suffix = string.IsNullOrWhiteSpace(itemType) ? "" : "?itemType=" + Uri.EscapeDataString(itemType);
        using var response = await _http.GetAsync(U("/api/rcs/inventory/models" + suffix), token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<RcsInventoryModelDto[]>(cancellationToken: token) ?? [];
    }

    public async Task<RcsInventoryModelDto?> SaveInventoryModelAsync(RcsInventoryModelDto model, CancellationToken token = default)
    {
        using var response = model.Id == 0
            ? await _http.PostAsJsonAsync(U("/api/rcs/inventory/models"), model, token)
            : await _http.PutAsJsonAsync(U($"/api/rcs/inventory/models/{model.Id}"), model, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<RcsInventoryModelDto>(cancellationToken: token);
    }

    public async Task DeleteInventoryModelAsync(long id, CancellationToken token = default)
    {
        using var response = await _http.DeleteAsync(U($"/api/rcs/inventory/models/{id}"), token);
        await EnsureSuccessAsync(response, token);
    }

    public async Task<IReadOnlyList<RcsInventoryInstanceDto>> GetInventoryInstancesAsync(
        string? itemType = null, string? mapCode = null, string? pointCode = null, CancellationToken token = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(itemType)) query.Add("itemType=" + Uri.EscapeDataString(itemType));
        if (!string.IsNullOrWhiteSpace(mapCode)) query.Add("mapCode=" + Uri.EscapeDataString(mapCode));
        if (!string.IsNullOrWhiteSpace(pointCode)) query.Add("pointCode=" + Uri.EscapeDataString(pointCode));
        var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
        using var response = await _http.GetAsync(U("/api/rcs/inventory/instances" + suffix), token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<RcsInventoryInstanceDto[]>(cancellationToken: token) ?? [];
    }

    public async Task<RcsInventoryInstanceDto?> CreateInventoryInstanceAsync(CreateRcsInventoryInstanceRequest request, CancellationToken token = default)
    {
        using var response = await _http.PostAsJsonAsync(U("/api/rcs/inventory/instances"), request, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<RcsInventoryInstanceDto>(cancellationToken: token);
    }

    public async Task<RcsInventoryInstanceDto?> MoveInventoryInstanceAsync(long id, MoveRcsInventoryInstanceRequest request, CancellationToken token = default)
    {
        using var response = await _http.PutAsJsonAsync(U($"/api/rcs/inventory/instances/{id}/move"), request, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<RcsInventoryInstanceDto>(cancellationToken: token);
    }

    public async Task DeleteInventoryInstanceAsync(long id, CancellationToken token = default)
    {
        using var response = await _http.DeleteAsync(U($"/api/rcs/inventory/instances/{id}"), token);
        await EnsureSuccessAsync(response, token);
    }

    private async Task<T?> PostAsync<T>(string url, object body, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync(U(url), body, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }

    private async Task<T?> PutAsync<T>(string url, object body, CancellationToken token)
    {
        using var response = await _http.PutAsJsonAsync(U(url), body, token);
        await EnsureSuccessAsync(response, token);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }

    private string U(string path) => _baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
}
