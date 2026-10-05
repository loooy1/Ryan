using Microsoft.AspNetCore.SignalR.Client;
using Contracts.Rcs.Vehicle;
using Contracts.Rcs.Tasks;

namespace Dashboard.Modules.RcsSimulator.Services;

public sealed class RcsRealtimeHubClient : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectedUrl;
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    private string _baseUrl = "http://localhost:8232";
    public event Action<VehicleStateDto>? VehicleStateChanged;
    public event Action<IReadOnlyList<VehicleStateDto>>? VehiclesChanged;
    public event Action<RcsTaskDto>? TaskChanged;
    public event Action? InventoryChanged;
    public event Action? Reconnected;

    public void SetBaseUrl(string url) => _baseUrl = url.Trim().TrimEnd('/');

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try { await StartCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync()
    {
        if (_connection is not null && _connectedUrl != _baseUrl)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        if (_connection is not null)
        {
            if (_connection.State != HubConnectionState.Disconnected) return;
            await _connection.DisposeAsync();
        }
        var url = _baseUrl;
        _connection = new HubConnectionBuilder()
            .WithUrl(url + "/hubs/rcs-realtime")
            .WithAutomaticReconnect()
            .Build();
        _connection.On<VehicleStateDto>("VehicleStateChanged", state => VehicleStateChanged?.Invoke(state));
        _connection.On<VehicleStateDto[]>("VehiclesChanged", states => VehiclesChanged?.Invoke(states));
        _connection.On<RcsTaskDto>("TaskChanged", task => TaskChanged?.Invoke(task));
        _connection.On("InventoryChanged", () => InventoryChanged?.Invoke());
        _connection.Reconnected += _ => { Reconnected?.Invoke(); return Task.CompletedTask; };
        _connectedUrl = url;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _connection.StartAsync(timeout.Token); }
        catch
        {
            await _connection.DisposeAsync();
            _connection = null;
            _connectedUrl = null;
            throw;
        }
    }

    public async Task RestartAsync(string baseUrl)
    {
        await _gate.WaitAsync();
        try
        {
            SetBaseUrl(baseUrl);
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
            await StartCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { if (_connection is not null) await _connection.DisposeAsync(); }
        finally { _gate.Release(); }
    }
}
