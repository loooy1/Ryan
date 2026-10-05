using Backend.Shared.Logging;
using Microsoft.AspNetCore.SignalR;
using RCSBackend.Modules.Rcs.Application.Execution;
using RCSBackend.Modules.Rcs.Application.Tasks;
using Contracts.Rcs.Tasks;
using RCSBackend.Modules.Rcs.Application.Vehicles;
using Contracts.Rcs.Vehicle;

namespace RCSBackend.Modules.Rcs.Realtime;

public sealed class RcsRealtimePublisher : IHostedService
{
    private readonly IRcsTaskExecutionService _execution;
    private readonly IHubContext<RcsRealtimeHub> _hub;
    private readonly LogEventBuffer _logs;
    private readonly IRcsTaskService _tasks;
    private readonly RcsVehicleRegistry _vehicles;

    public RcsRealtimePublisher(IRcsTaskExecutionService execution, IHubContext<RcsRealtimeHub> hub,
        LogEventBuffer logs, IRcsTaskService tasks, RcsVehicleRegistry vehicles)
    {
        _execution = execution;
        _hub = hub;
        _logs = logs;
        _tasks = tasks;
        _vehicles = vehicles;
        _vehicles.VehiclesChanged += OnVehiclesChanged;
        _tasks.TaskChanged += OnTaskChanged;
        _execution.VehicleStateChanged += OnStateChanged;
        _execution.InventoryChanged += OnInventoryChanged;
        _logs.EventAdded += OnLogAdded;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _execution.VehicleStateChanged -= OnStateChanged;
        _execution.InventoryChanged -= OnInventoryChanged;
        _vehicles.VehiclesChanged -= OnVehiclesChanged;
        _tasks.TaskChanged -= OnTaskChanged;
        _logs.EventAdded -= OnLogAdded;
        return Task.CompletedTask;
    }

    private void OnStateChanged(global::Contracts.Rcs.Vehicle.VehicleStateDto state) =>
        _ = _hub.Clients.All.SendAsync("VehicleStateChanged", state);

    private void OnInventoryChanged() => _ = _hub.Clients.All.SendAsync("InventoryChanged");

    private void OnVehiclesChanged(IReadOnlyList<VehicleStateDto> states) =>
        _ = _hub.Clients.All.SendAsync("VehiclesChanged", _execution.GetVehicles());

    private void OnTaskChanged(RcsTaskDto task) => _ = _hub.Clients.All.SendAsync("TaskChanged", task);

    private void OnLogAdded(AppLogEvent entry)
    {
        // 推送动作本身可能产生 SignalR 框架日志；过滤它们，避免日志推送递归。
        if (entry.SourceContext.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.OrdinalIgnoreCase)
            || entry.SourceContext.Equals(typeof(RcsRealtimePublisher).FullName, StringComparison.OrdinalIgnoreCase))
            return;
        _ = _hub.Clients.All.SendAsync("LogAdded", entry);
    }
}
