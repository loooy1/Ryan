using System.Collections.Concurrent;
using Backend.Shared.Logging;
using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;
using RCSBackend.Modules.Rcs.Application.Vehicles;

namespace RCSBackend.Modules.Rcs.Protocol;

/// <summary>统一命令路由：按车辆配置找到协议会话，RCS 执行层不感知具体车型和传输方式。</summary>
public sealed class VehicleCommandChannel : IVehicleCommandChannel, IDisposable
{
    private readonly RcsVehicleRegistry _registry;
    private readonly ILogger<VehicleCommandChannel> _logger;
    private readonly ConcurrentDictionary<string, (string TaskId, int Count)> _progress = new();

    public VehicleCommandChannel(RcsVehicleRegistry registry, ILogger<VehicleCommandChannel> logger)
    {
        _registry = registry; _logger = logger;
        registry.StateChanged += OnStateChanged;
        registry.VehiclesChanged += OnVehiclesChanged;
    }

    public event Action<VehicleStateDto>? StateChanged;
    public IReadOnlyList<VehicleStateDto> GetStates() => _registry.GetStates();

    public async Task<VehicleCommandAck> SendAsync(VehicleCommand command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var session = _registry.GetVehicle(command.VehicleId);
        var ack = await session.SendAsync(command, token);
        using var scope = Scope(command.TaskId);
        _logger.Log(ack.Accepted ? LogLevel.Information : LogLevel.Warning,
            "车辆协议命令 {Type} {Result} Vehicle={Vehicle} Protocol={Protocol} CommandId={CommandId} RouteVersion={Version} Points={Points} Reason={Reason}",
            command.Type, ack.Accepted ? "已接收" : "被拒绝", command.VehicleId,
            _registry.GetDefinition(command.VehicleId).Protocol, command.CommandId,
            command.RouteVersion, command.Points?.Count ?? 0, ack.Message);
        return ack;
    }

    public async Task<VehicleCommandResult> WaitForCompletionAsync(string vehicleId, string commandId, CancellationToken token = default)
    {
        var result = await _registry.GetVehicle(vehicleId).WaitForCompletionAsync(commandId, token);
        using var scope = Scope(result.TaskId);
        _logger.Log(result.Status == "FAILED" ? LogLevel.Error : LogLevel.Information,
            "车辆动作执行结果 {Status} Vehicle={Vehicle} CommandId={CommandId} RouteVersion={Version} Reason={Reason}",
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
            _logger.LogInformation("车辆确认点位动作 Vehicle={Vehicle} Point={Point} Action={Action} LoadedContainer={Container} Step={Step}",
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
    {
        _registry.StateChanged -= OnStateChanged;
        _registry.VehiclesChanged -= OnVehiclesChanged;
    }
}
