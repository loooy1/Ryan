using System.Collections.Concurrent;
using Backend.Shared.Logging;
using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;
using RCSBackend.Modules.Rcs.Application.Vehicles;

namespace RCSBackend.Modules.Rcs.Protocol;

/// <summary>根据 VehicleId 路由到独立虚拟车，车辆状态和命令完成均按车辆隔离。</summary>
public sealed class InProcessVehicleCommandChannel : IVehicleCommandChannel, IDisposable
{
    private readonly RcsVehicleRegistry _registry;
    private readonly ILogger<InProcessVehicleCommandChannel> _logger;
    private readonly ConcurrentDictionary<string, (string TaskId, int Count)> _progress = new();
    public InProcessVehicleCommandChannel(RcsVehicleRegistry registry, ILogger<InProcessVehicleCommandChannel> logger)
    { _registry = registry; _logger = logger; registry.StateChanged += OnStateChanged; registry.VehiclesChanged += OnVehiclesChanged; }

    public event Action<VehicleStateDto>? StateChanged;
    public IReadOnlyList<VehicleStateDto> GetStates() => _registry.GetStates();

    public Task<VehicleCommandAck> SendAsync(VehicleCommand command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ack = _registry.GetVehicle(command.VehicleId).Receive(command);
        using var scope = Scope(command.TaskId);
        _logger.Log(ack.Accepted ? LogLevel.Information : LogLevel.Warning,
            "车辆命令 {Type} {Result} Vehicle={Vehicle} CommandId={CommandId} RouteVersion={Version} Points={Points} Reason={Reason}",
            command.Type, ack.Accepted ? "已接收" : "被拒绝", command.VehicleId, command.CommandId,
            command.RouteVersion, command.Points?.Count ?? 0, ack.Message);
        return Task.FromResult(ack);
    }

    public async Task<VehicleCommandResult> WaitForCompletionAsync(string vehicleId, string commandId, CancellationToken token = default)
    {
        var result = await _registry.GetVehicle(vehicleId).WaitForCompletionAsync(commandId, token);
        using var scope = Scope(result.TaskId);
        _logger.Log(result.Status == "FAILED" ? LogLevel.Error : LogLevel.Information,
            "车辆执行结果 {Status} Vehicle={Vehicle} CommandId={CommandId} RouteVersion={Version} Reason={Reason}",
            result.Status, vehicleId, result.CommandId, result.RouteVersion, result.Message);
        return result;
    }

    private void OnStateChanged(VehicleStateDto state)
    {
        var old = _progress.GetValueOrDefault(state.Id);
        if (old.TaskId != state.TaskId) old = (state.TaskId, 0);
        if (state.CompletedStepIds.Count > old.Count)
        {
            using var scope = Scope(state.TaskId);
            _logger.LogInformation("车辆站点动作完成 Vehicle={Vehicle} Point={Point} Action={Action} LoadedContainer={Container} Step={Step}",
                state.Id, state.PointCode, state.LastAction, state.LoadedContainerCode, state.CompletedStepIds[^1]);
        }
        _progress[state.Id] = (state.TaskId, state.CompletedStepIds.Count);
        StateChanged?.Invoke(state);
    }
    private void OnVehiclesChanged(IReadOnlyList<VehicleStateDto> states)
    {
        foreach (var id in _progress.Keys.Where(id => !states.Any(x => x.Id == id))) _progress.TryRemove(id, out _);
    }
    private IDisposable? Scope(string taskId) => _logger.BeginScope(new Dictionary<string, object?>
        { ["LogCategory"] = LogCategory.Task.ToString(), ["TaskId"] = taskId });
    public void Dispose()
    { _registry.StateChanged -= OnStateChanged; _registry.VehiclesChanged -= OnVehiclesChanged; }
}
