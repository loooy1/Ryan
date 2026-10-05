using System.Text.RegularExpressions;
using Backend.Shared.Logging;
using Contracts.Rcs.Vehicle;
using Contracts.Rcs.Tasks;
using Rcs.Algorithms.Traffic;
using RCSBackend.Modules.Rcs.Application.Scheduling;
using RCSBackend.Modules.Rcs.Application.Tasks;
using RCSBackend.Modules.Rcs.Application.Execution;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Application.Vehicles;

/// <summary>管理车辆配置和增删限制；控制动作仍由任务服务协调。</summary>
public sealed class RcsVehicleService(IRcsVehicleStore store, RcsVehicleRegistry registry,
    IRcsTaskStore taskStore, IRcsTaskService tasks, RcsDispatchLock dispatchLock, RcsMapCache maps,
    IMultiVehicleTrafficCoordinator traffic, ILogger<RcsVehicleService> logger)
{
    public IReadOnlyList<VehicleProtocolInfoDto> SupportedProtocols => registry.SupportedProtocols;

    public async Task<VehicleStateDto> ReceiveHeartbeatAsync(string id, ReadOnlyMemory<byte> payload,
        CancellationToken token = default)
    {
        await registry.InitializeAsync(token);
        return registry.ReceiveHeartbeat(id, payload);
    }

    public async Task<IReadOnlyList<VehicleStateDto>> ListAsync(CancellationToken token = default)
    {
        await registry.InitializeAsync(token);
        var states = registry.GetStates();
        foreach (var state in states)
        {
            traffic.ObserveVehiclePosition(new Contracts.Rcs.Algorithm.AlgorithmVehiclePositionDto(
                state.Id, state.PointCode, state.Status is "Running" or "Paused", DateTimeOffset.UtcNow));
            if (state.Status is not ("Running" or "Paused")) traffic.ObserveStationaryPosition(state.Id, state.PointCode);
        }
        return states;
    }

    public async Task<VehicleStateDto> AddAsync(CreateVehicleRequest request, CancellationToken token = default)
    {
        if (request is null || string.IsNullOrEmpty(request.Id) || !Regex.IsMatch(request.Id, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"))
            throw new ArgumentException("车辆编号为 1～64 位字母、数字、下划线或短横线，首位须为字母或数字。");
        ValidateName(request.Name);
        if (request.PointCode is null || request.PointCode.Length > 128) throw new ArgumentException("初始站点编码非法。");
        await registry.InitializeAsync(token);
        await dispatchLock.Gate.WaitAsync(token);
        try
        {
            foreach (var vehicle in registry.GetStates().Where(x => x.Status is not ("Running" or "Paused")))
            {
                traffic.ObserveVehiclePosition(new Contracts.Rcs.Algorithm.AlgorithmVehiclePositionDto(
                    vehicle.Id, vehicle.PointCode, false, DateTimeOffset.UtcNow));
                traffic.ObserveStationaryPosition(vehicle.Id, vehicle.PointCode);
            }
            if (registry.Contains(request.Id)) throw new RcsTaskConflictException("车辆编号已存在。");
            if (request.PointCode != "" && maps.Current?.Points.ContainsKey(request.PointCode) != true)
                throw new ArgumentException("初始站点不存在或已禁用。");
            var protocol = NormalizeProtocol(request.Protocol);
            EnsureProtocolSupported(protocol);
            var now = DateTime.UtcNow;
            var operatingMode = NormalizeOperatingMode(request.OperatingMode);
            var row = new RcsVehicleRow { VehicleId = request.Id, Name = request.Name.Trim(), InitialPointCode = request.PointCode,
                Protocol = protocol, OperatingMode = operatingMode, IsEnabled = request.IsEnabled, CreatedAt = now, UpdatedAt = now };
            if (row.Name == "") row.Name = row.VehicleId;
            if (!traffic.TryAcquirePosition(row.VehicleId, "", row.InitialPointCode,
                out var positionLease, out var conflictingVehicle))
                throw new RcsTaskConflictException($"初始站点 {row.InitialPointCode} 已被车辆 {conflictingVehicle} 占用或锁定。");
            using var heldPosition = positionLease!;
            await store.AddAsync(row, token);
            registry.Add(row); heldPosition.Commit();
            traffic.ObserveVehiclePosition(new Contracts.Rcs.Algorithm.AlgorithmVehiclePositionDto(
                row.VehicleId, row.InitialPointCode, false, DateTimeOffset.UtcNow));
            tasks.NotifyWork();
            Log("车辆已添加", row.VehicleId);
            return registry.GetStates().Single(x => x.Id == row.VehicleId);
        }
        finally { dispatchLock.Gate.Release(); }
    }

    public async Task<VehicleStateDto> UpdateAsync(string id, UpdateVehicleRequest request, CancellationToken token = default)
    {
        ValidateName(request.Name);
        await registry.InitializeAsync(token);
        await dispatchLock.Gate.WaitAsync(token);
        try
        {
            var row = registry.GetDefinition(id);
            var protocol = string.IsNullOrWhiteSpace(request.Protocol) ? row.Protocol : NormalizeProtocol(request.Protocol);
            EnsureProtocolSupported(protocol);
            if (!string.Equals(protocol, row.Protocol, StringComparison.OrdinalIgnoreCase))
            {
                var current = registry.GetStates().Single(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (tasks.IsVehicleBusy(id) || current.Status is "Running" or "Paused" || current.LoadedContainerCode != "")
                    throw new RcsTaskConflictException("车辆执行任务或载有托盘时不能切换协议。");
                row.InitialPointCode = current.PointCode;
            }
            var operatingMode = string.IsNullOrWhiteSpace(request.OperatingMode)
                ? row.OperatingMode : NormalizeOperatingMode(request.OperatingMode);
            row.Name = request.Name.Trim() == "" ? row.VehicleId : request.Name.Trim();
            row.Protocol = protocol; row.OperatingMode = operatingMode; row.IsEnabled = request.IsEnabled; row.UpdatedAt = DateTime.UtcNow;
            await store.UpdateAsync(row, token); registry.Update(row); tasks.NotifyWork();
            Log(row.IsEnabled ? $"车辆配置已保存，运行模式={row.OperatingMode}" : "车辆已停用，当前任务继续执行，后续任务不再分配", row.VehicleId);
            return registry.GetStates().Single(x => x.Id == row.VehicleId);
        }
        finally { dispatchLock.Gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken token = default)
    {
        await registry.InitializeAsync(token);
        await dispatchLock.Gate.WaitAsync(token);
        try
        {
            var row = registry.GetDefinition(id);
            var state = registry.GetStates().Single(x => x.Id == row.VehicleId);
            if (tasks.IsVehicleBusy(row.VehicleId) || state.Status is "Running" or "Paused")
                throw new RcsTaskConflictException("车辆仍有执行任务，请先停止并等待任务结束。");
            if (state.LoadedContainerCode != "") throw new RcsTaskConflictException("车辆仍载有容器，不能删除。");
            var waiting = await taskStore.WaitingCandidatesAsync(RcsTaskSource.Upstream, token);
            waiting = waiting.Concat(await taskStore.WaitingCandidatesAsync(RcsTaskSource.Manual, token)).ToArray();
            if (waiting.Any(x =>
                string.Equals(x.RequestedVehicleId, row.VehicleId, StringComparison.OrdinalIgnoreCase)))
                throw new RcsTaskConflictException("车辆有指定给它的等待任务，请先取消这些任务。");
            await store.DeleteAsync(row.VehicleId, token); registry.Remove(row.VehicleId);
            traffic.RemoveVehicle(row.VehicleId); tasks.NotifyWork();
            Log("车辆已删除", row.VehicleId);
        }
        finally { dispatchLock.Gate.Release(); }
    }

    public async Task ResetAsync(string id, string pointCode, CancellationToken token = default)
    {
        await registry.InitializeAsync(token);
        await tasks.ResetVehicleAsync(pointCode, token, id, async () => {
            var row = registry.GetDefinition(id);
            row.InitialPointCode = pointCode; row.UpdatedAt = DateTime.UtcNow;
            await store.UpdateAsync(row, token); registry.Update(row);
        });
    }
    public async Task<VehicleStateDto> SetPositionAsync(string id, string pointCode, CancellationToken token = default)
    {
        if (pointCode is null || pointCode.Length > 128) throw new ArgumentException("当前位置站点编码非法。");
        if (pointCode != "" && maps.Current?.Points.ContainsKey(pointCode) != true)
            throw new ArgumentException("当前位置站点不存在或已禁用。");
        await registry.InitializeAsync(token);
        await tasks.SetVehiclePositionAsync(pointCode, token, id, async () =>
        {
            var row = registry.GetDefinition(id);
            row.InitialPointCode = pointCode; row.UpdatedAt = DateTime.UtcNow;
            await store.UpdateAsync(row, token); registry.Update(row);
        });
        return registry.GetStates().Single(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }
    private static void ValidateName(string? name)
    { if (name is null || name.Length > 128) throw new ArgumentException("车辆名称最多 128 字符。"); }
    private static string NormalizeOperatingMode(string? mode) => mode?.Trim().ToUpperInvariant() switch
    {
        RcsOperatingMode.Automatic => RcsOperatingMode.Automatic,
        RcsOperatingMode.Manual => RcsOperatingMode.Manual,
        _ => throw new ArgumentException("车辆运行模式只能是 AUTO 或 MANUAL。")
    };
    private static string NormalizeProtocol(string? protocol) => string.IsNullOrWhiteSpace(protocol)
        ? "VIRTUAL" : protocol.Trim().ToUpperInvariant();
    private void EnsureProtocolSupported(string protocol)
    {
        if (!registry.SupportsProtocol(protocol))
            throw new ArgumentException($"未注册车辆协议 {protocol}。请先实现并注册对应的协议适配器。");
    }
    private void Log(string message, string id)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["LogCategory"] = LogCategory.System.ToString() });
        logger.LogInformation("{Message} VehicleId={VehicleId}", message, id);
    }
}
