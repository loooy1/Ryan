using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Map;

namespace Rcs.Algorithms.Traffic;

public interface IMultiVehicleTrafficCoordinator
{
    void ObserveVehiclePosition(AlgorithmVehiclePositionDto position);
    void SetVehicleGoals(string vehicleId, IReadOnlyList<string> goalPointCodes);
    void RemoveVehicle(string vehicleId);
    AlgorithmPlanningContextDto GetPlanningContext(string vehicleId);
    bool ObserveStationaryPosition(string vehicleId, string pointCode);
    void ObserveProgress(string vehicleId, string routeId, int routeVersion, int routeIndex, string pointCode);
    IReadOnlyList<string> GetLockedPoints(string vehicleId);
    IReadOnlyList<string> GetLockedLines(string vehicleId);
    bool TryAcquireRouteWindow(RcsMapSnapshot map, string vehicleId, string routeId,
        IReadOnlyList<string> pointCodes, int routePointOffset, int routeVersion,
        out IAlgorithmRouteLease? lease, out string? conflictingVehicle);
    Task<IAlgorithmRouteLease> AcquireRouteWindowAsync(RcsMapSnapshot map, string vehicleId, string routeId,
        IReadOnlyList<string> pointCodes, int routePointOffset, int routeVersion,
        CancellationToken token = default);
    bool TryAcquirePosition(string vehicleId, string routeId, string pointCode,
        out IAlgorithmRouteLease? lease, out string? conflictingVehicle);
    Task<IAlgorithmRouteLease> AcquirePositionAsync(string vehicleId, string routeId, string pointCode,
        CancellationToken token = default);
}

public interface IAlgorithmRouteLease : IDisposable
{
    void Commit();
}
