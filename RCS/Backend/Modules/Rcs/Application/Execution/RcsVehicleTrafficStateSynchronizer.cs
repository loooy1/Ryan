using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;
using Rcs.Algorithms.Traffic;

namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>Projects vehicle telemetry into the algorithm's fleet context and lock state.</summary>
public sealed class RcsVehicleTrafficStateSynchronizer(
    IMultiVehicleTrafficCoordinator traffic,
    ILogger<RcsVehicleTrafficStateSynchronizer> logger)
{
    public void ClearGoal(string vehicleId) => traffic.SetVehicleGoals(vehicleId, []);

    public void SyncFleet(IReadOnlyList<VehicleStateDto> states)
    {
        foreach (var state in states)
        {
            ObservePosition(state);
            if (state.Status is not ("Running" or "Paused")) ObserveStationaryPosition(state);
        }
    }

    public void ObservePosition(VehicleStateDto state) => traffic.ObserveVehiclePosition(
        new AlgorithmVehiclePositionDto(state.Id, state.PointCode,
            state.Status is "Running" or "Paused", DateTimeOffset.UtcNow));

    public void ObserveStationaryPosition(VehicleStateDto state)
    {
        if (!traffic.ObserveStationaryPosition(state.Id, state.PointCode) && !string.IsNullOrWhiteSpace(state.PointCode))
            logger.LogError("车辆当前位置与另一车辆的地图锁冲突 Vehicle={Vehicle} Point={Point}", state.Id, state.PointCode);
    }

    public VehicleStateDto EnrichState(VehicleStateDto state)
    {
        var points = traffic.GetLockedPoints(state.Id);
        var lines = traffic.GetLockedLines(state.Id);
        return state with
        {
            LockPointCode = state.Status is "Running" or "Paused" && points.Count > 0
                ? points.Last() : "",
            LockedPointCodes = points,
            LockedLineCodes = lines
        };
    }
}
