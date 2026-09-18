using Microsoft.AspNetCore.SignalR;
using RCSBackend.Modules.Rcs.Infrastructure;

namespace RCSBackend.Modules.Rcs.Realtime;

public sealed class RcsRealtimePublisher : IHostedService
{
    private readonly RcsSimulationService _simulation;
    private readonly IHubContext<RcsRealtimeHub> _hub;

    public RcsRealtimePublisher(RcsSimulationService simulation, IHubContext<RcsRealtimeHub> hub)
    {
        _simulation = simulation;
        _hub = hub;
        _simulation.Vehicle.StateChanged += OnStateChanged;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _simulation.Vehicle.StateChanged -= OnStateChanged;
        return Task.CompletedTask;
    }

    private void OnStateChanged(global::Rcs.Contracts.Vehicles.VehicleStateDto state) =>
        _ = _hub.Clients.All.SendAsync("VehicleStateChanged", state);
}
