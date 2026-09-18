using Microsoft.AspNetCore.SignalR.Client;
using Rcs.Contracts.Vehicles;

namespace Dashboard.Modules.RcsSimulator.Services;

public sealed class RcsRealtimeHubClient : IAsyncDisposable
{
    private HubConnection? _connection;
    public event Action<VehicleStateDto>? VehicleStateChanged;

    public async Task StartAsync()
    {
        if (_connection is not null) return;
        _connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:8231/hubs/rcs-realtime")
            .WithAutomaticReconnect()
            .Build();
        _connection.On<VehicleStateDto>("VehicleStateChanged", state => VehicleStateChanged?.Invoke(state));
        await _connection.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
