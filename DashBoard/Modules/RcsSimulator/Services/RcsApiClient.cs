using System.Net.Http.Json;
using Rcs.Contracts.Map;
using Rcs.Contracts.Vehicles;

namespace Dashboard.Modules.RcsSimulator.Services;

public sealed class RcsApiClient
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://localhost:8231"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    public Task<GridMapDto?> GetMapAsync(CancellationToken token = default) =>
        _http.GetFromJsonAsync<GridMapDto>("/api/rcs/map", token);

    public async Task<VehicleStateDto?> GetVehicleAsync(CancellationToken token = default)
    {
        var vehicles = await _http.GetFromJsonAsync<VehicleStateDto[]>("/api/rcs/vehicles", token);
        return vehicles?.FirstOrDefault();
    }

    public Task<RouteDto?> PreviewAsync(RunVehicleRequest request, CancellationToken token = default) =>
        PostAsync<RouteDto>("/api/rcs/routes/preview", request, token);

    public Task<RouteDto?> RunAsync(RunVehicleRequest request, CancellationToken token = default) =>
        PostAsync<RouteDto>("/api/rcs/vehicles/V-01/run", request, token);

    public Task PauseAsync(CancellationToken token = default) =>
        _http.PostAsync("/api/rcs/vehicles/V-01/pause", null, token);

    public Task ResetAsync(GridPoint position, CancellationToken token = default) =>
        _http.PostAsJsonAsync("/api/rcs/vehicles/V-01/reset", position, token);

    private async Task<T?> PostAsync<T>(string url, object body, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync(url, body, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }
}
