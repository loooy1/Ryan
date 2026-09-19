using Backend.Shared.Logging;
using Microsoft.AspNetCore.SignalR;
using RCSBackend.Modules.Rcs.Infrastructure;

namespace RCSBackend.Modules.Rcs.Realtime;

public sealed class RcsRealtimePublisher : IHostedService
{
    private readonly RcsSimulationService _simulation;
    private readonly IHubContext<RcsRealtimeHub> _hub;
    private readonly LogEventBuffer _logs;

    public RcsRealtimePublisher(RcsSimulationService simulation, IHubContext<RcsRealtimeHub> hub,
        LogEventBuffer logs)
    {
        _simulation = simulation;
        _hub = hub;
        _logs = logs;
        _simulation.Vehicle.StateChanged += OnStateChanged;
        _logs.EventAdded += OnLogAdded;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _simulation.Vehicle.StateChanged -= OnStateChanged;
        _logs.EventAdded -= OnLogAdded;
        return Task.CompletedTask;
    }

    private void OnStateChanged(global::Contracts.Rcs.Vehicle.VehicleStateDto state) =>
        _ = _hub.Clients.All.SendAsync("VehicleStateChanged", state);

    private void OnLogAdded(AppLogEvent entry)
    {
        // 推送动作本身可能产生 SignalR 框架日志；过滤它们，避免日志推送递归。
        if (entry.SourceContext.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.OrdinalIgnoreCase)
            || entry.SourceContext.Equals(typeof(RcsRealtimePublisher).FullName, StringComparison.OrdinalIgnoreCase))
            return;
        _ = _hub.Clients.All.SendAsync("LogAdded", entry);
    }
}
