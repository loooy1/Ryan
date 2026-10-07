using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Map;

namespace Rcs.Algorithms.Traffic;

/// <summary>多车交通控制的进程内实现：接收车辆位置/目标，统一规划上下文与路径资源预约。</summary>
public sealed class MultiVehicleTrafficCoordinator : IMultiVehicleTrafficCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Reservation> _visible = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _waitingFor = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AlgorithmVehiclePositionDto> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _goals = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>RCS 将车辆遥测投递到交通控制层；只保留规划需要的位置与运动状态。</summary>
    public void ObserveVehiclePosition(AlgorithmVehiclePositionDto position)
    {
        if (string.IsNullOrWhiteSpace(position.VehicleId)) return;
        lock (_gate) _positions[position.VehicleId] = position;
    }

    /// <summary>RCS完成任务分配后只登记该车的目标点，不传任务类型或点位动作。</summary>
    public void SetVehicleGoals(string vehicleId, IReadOnlyList<string> goalPointCodes)
    {
        lock (_gate)
        {
            if (goalPointCodes.Count == 0) _goals.Remove(vehicleId);
            else _goals[vehicleId] = goalPointCodes.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        }
    }

    public void RemoveVehicle(string vehicleId)
    {
        lock (_gate)
        {
            _positions.Remove(vehicleId);
            _goals.Remove(vehicleId);
            _waitingFor.Remove(vehicleId);
            if (_visible.TryGetValue(vehicleId, out var reservation))
                foreach (var resource in reservation.Resources)
                    if (_owners.TryGetValue(resource, out var owner) && Same(owner, vehicleId)) _owners.Remove(resource);
            _visible.Remove(vehicleId);
            SignalChanged();
        }
    }

    public AlgorithmPlanningContextDto GetPlanningContext(string vehicleId)
    {
        lock (_gate)
        {
            var vehicles = _positions.Values.Select(position => new AlgorithmVehiclePlanningStateDto(
                position.VehicleId, position.PointCode,
                _goals.TryGetValue(position.VehicleId, out var goals) ? goals : Array.Empty<string>(),
                position.IsMoving)).ToArray();
            var reservations = _visible.Where(x => !Same(x.Key, vehicleId)).Select(x => x.Value).ToArray();
            var occupiedPoints = _positions.Values.Where(x => !Same(x.VehicleId, vehicleId)
                    && !string.IsNullOrWhiteSpace(x.PointCode))
                .Select(x => x.PointCode);
            return new AlgorithmPlanningContextDto(vehicleId, vehicles,
                reservations.SelectMany(x => x.PointCodes).Concat(occupiedPoints)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                reservations.SelectMany(x => x.LineCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public bool TryAcquire(string vehicleId, string taskId, IEnumerable<string> resources,
        IReadOnlyList<string> pointCodes, IReadOnlyList<string> lineCodes,
        out Lease? lease, out string? conflictingVehicle, int routePointOffset = 0, int routeVersion = 0)
    {
        var requested = resources.Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            conflictingVehicle = requested.Select(resource => _owners.TryGetValue(resource, out var owner) ? owner : null)
                .FirstOrDefault(owner => owner is not null && !Same(owner, vehicleId));
            if (conflictingVehicle is not null) { lease = null; return false; }

            var previous = _visible.TryGetValue(vehicleId, out var current)
                ? current.Resources.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in requested) _owners[resource] = vehicleId;
            lease = new Lease(this, vehicleId, taskId, requested, previous, pointCodes, lineCodes,
                routePointOffset, routeVersion);
            return true;
        }
    }

    public bool TryAcquireRouteWindow(RcsMapSnapshot map, string vehicleId, string routeId,
        IReadOnlyList<string> pointCodes, int routePointOffset, int routeVersion,
        out IAlgorithmRouteLease? lease, out string? conflictingVehicle)
    {
        var resources = GetRouteResources(map, pointCodes);
        var acquired = TryAcquire(vehicleId, routeId, resources.All, resources.Points, resources.Lines,
            out var concreteLease, out conflictingVehicle, routePointOffset, routeVersion);
        lease = concreteLease;
        return acquired;
    }

    public async Task<IAlgorithmRouteLease> AcquireRouteWindowAsync(RcsMapSnapshot map, string vehicleId,
        string routeId, IReadOnlyList<string> pointCodes, int routePointOffset, int routeVersion,
        CancellationToken token = default)
    {
        var resources = GetRouteResources(map, pointCodes);
        return await AcquireAsync(vehicleId, routeId, resources.All, resources.Points, resources.Lines,
            routePointOffset, routeVersion, token);
    }

    public bool TryAcquirePosition(string vehicleId, string routeId, string pointCode,
        out IAlgorithmRouteLease? lease, out string? conflictingVehicle)
    {
        var points = string.IsNullOrWhiteSpace(pointCode) ? Array.Empty<string>() : new[] { pointCode };
        var acquired = TryAcquire(vehicleId, routeId, points.Select(PointResource), points, [],
            out var concreteLease, out conflictingVehicle, 0, 0);
        lease = concreteLease;
        return acquired;
    }

    public async Task<IAlgorithmRouteLease> AcquirePositionAsync(string vehicleId, string routeId,
        string pointCode, CancellationToken token = default)
    {
        var points = string.IsNullOrWhiteSpace(pointCode) ? Array.Empty<string>() : new[] { pointCode };
        return await AcquireAsync(vehicleId, routeId, points.Select(PointResource), points, [], 0, 0, token);
    }

    public async Task<Lease> AcquireAsync(string vehicleId, string taskId, IEnumerable<string> resources,
        IReadOnlyList<string> pointCodes, IReadOnlyList<string> lineCodes,
        int routePointOffset, int routeVersion, CancellationToken token = default)
    {
        var requested = resources.Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task waitForChange;
            lock (_gate)
            {
                if (requested.All(resource => !_owners.TryGetValue(resource, out var owner)
                    || Same(owner, vehicleId)))
                {
                    _waitingFor.Remove(vehicleId);
                    var previous = _visible.TryGetValue(vehicleId, out var current)
                        ? current.Resources.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var resource in requested) _owners[resource] = vehicleId;
                    return new Lease(this, vehicleId, taskId, requested, previous, pointCodes, lineCodes,
                        routePointOffset, routeVersion);
                }

                var blockers = requested.Select(resource => _owners.TryGetValue(resource, out var owner) ? owner : null)
                    .Where(owner => owner is not null && !Same(owner, vehicleId))
                    .Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                _waitingFor[vehicleId] = blockers;
                if (blockers.Any(owner => Reaches(owner, vehicleId, new HashSet<string>(StringComparer.OrdinalIgnoreCase))))
                {
                    _waitingFor.Remove(vehicleId);
                    throw new InvalidOperationException($"车辆 {vehicleId} 的路径锁申请形成循环等待；为避免车辆互相等待，已拒绝本次路径续发。");
                }
                waitForChange = _changed.Task;
            }
            try { await waitForChange.WaitAsync(token); }
            catch
            {
                lock (_gate) _waitingFor.Remove(vehicleId);
                throw;
            }
        }
    }

    public IReadOnlyList<string> GetLockedPoints(string vehicleId) => GetVisible(vehicleId, "P:");
    public IReadOnlyList<string> GetLockedLines(string vehicleId) => GetVisible(vehicleId, "L:");

    /// <summary>Release resources strictly behind the vehicle's confirmed position in the active route window.</summary>
    public void ObserveProgress(string vehicleId, string taskId, int routeVersion, int routeIndex, string pointCode)
    {
        lock (_gate)
        {
            if (!_visible.TryGetValue(vehicleId, out var reservation)
                || !Same(reservation.TaskId, taskId) || reservation.RouteVersion != routeVersion
                || reservation.PointCodes.Count == 0) return;

            var currentIndex = routeIndex - 1;
            var localIndex = currentIndex - reservation.RoutePointOffset;
            if (localIndex <= 0 || localIndex >= reservation.PointCodes.Count
                || !Same(reservation.PointCodes[localIndex], pointCode)) return;

            var remainingPoints = reservation.PointCodes.Skip(localIndex).ToArray();
            var remainingLines = reservation.LineCodes.Skip(Math.Min(localIndex, reservation.LineCodes.Count)).ToArray();
            var resources = remainingPoints.Select(PointResource).Concat(remainingLines.Select(LineResource))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ReplaceVisible(vehicleId, reservation.TaskId, resources, remainingPoints, remainingLines,
                reservation.RoutePointOffset + localIndex, reservation.RouteVersion);
            SignalChanged();
        }
    }

    /// <summary>Idle/arrived vehicles continue to reserve the point they physically occupy.</summary>
    public bool ObserveStationaryPosition(string vehicleId, string pointCode)
    {
        lock (_gate)
        {
            var desired = string.IsNullOrWhiteSpace(pointCode)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>([PointResource(pointCode)], StringComparer.OrdinalIgnoreCase);
            if (desired.Any(resource => _owners.TryGetValue(resource, out var owner) && !Same(owner, vehicleId)))
                return false;

            ReplaceVisible(vehicleId, "", desired, string.IsNullOrWhiteSpace(pointCode) ? [] : [pointCode], [], 0, 0);
            SignalChanged();
            return true;
        }
    }

    public static string PointResource(string pointCode) => "P:" + pointCode;
    public static string LineResource(string lineCode) => "L:" + lineCode;

    public static (IReadOnlyList<string> All, IReadOnlyList<string> Points, IReadOnlyList<string> Lines)
        GetRouteResources(RcsMapSnapshot map, IReadOnlyList<string> pointCodes)
    {
        var lineCodes = new List<string>();
        for (var index = 1; index < pointCodes.Count; index++)
        {
            var from = pointCodes[index - 1];
            var to = pointCodes[index];
            if (Same(from, to)) continue;
            var edge = map.Adjacency.TryGetValue(from, out var edges)
                ? edges.FirstOrDefault(candidate => Same(candidate.ToPointCode, to)) : null;
            if (edge is null) throw new InvalidOperationException($"路径段缺少地图连线：{from} → {to}。");
            lineCodes.Add(edge.LineCode);
        }
        var all = pointCodes.Select(PointResource).Concat(lineCodes.Select(LineResource))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (all, pointCodes, lineCodes);
    }

    private IReadOnlyList<string> GetVisible(string vehicleId, string prefix)
    {
        lock (_gate)
        {
            if (!_visible.TryGetValue(vehicleId, out var reservation)) return [];
            return prefix == "P:" ? reservation.PointCodes : reservation.LineCodes;
        }
    }

    private void Commit(Lease lease)
    {
        lock (_gate)
        {
            ReplaceVisible(lease.VehicleId, lease.TaskId, lease.Resources, lease.PointCodes, lease.LineCodes,
                lease.RoutePointOffset, lease.RouteVersion);
            SignalChanged();
        }
    }

    private void Rollback(Lease lease)
    {
        lock (_gate)
        {
            foreach (var resource in lease.Resources.Except(lease.PreviousResources, StringComparer.OrdinalIgnoreCase))
                if (_owners.TryGetValue(resource, out var owner) && Same(owner, lease.VehicleId))
                    _owners.Remove(resource);
            SignalChanged();
        }
    }

    private void ReplaceVisible(string vehicleId, string taskId, HashSet<string> desired,
        IReadOnlyList<string> pointCodes, IReadOnlyList<string> lineCodes,
        int routePointOffset, int routeVersion)
    {
        if (_visible.TryGetValue(vehicleId, out var previous))
        {
            foreach (var resource in previous.Resources.Except(desired, StringComparer.OrdinalIgnoreCase))
                if (_owners.TryGetValue(resource, out var owner) && Same(owner, vehicleId))
                    _owners.Remove(resource);
        }
        foreach (var resource in desired) _owners[resource] = vehicleId;
        if (desired.Count == 0) _visible.Remove(vehicleId);
        else _visible[vehicleId] = new Reservation(taskId, desired.ToHashSet(StringComparer.OrdinalIgnoreCase),
            pointCodes.ToArray(), lineCodes.ToArray(), routePointOffset, routeVersion);
    }

    private void SignalChanged()
    {
        _waitingFor.Clear();
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private bool Reaches(string current, string target, HashSet<string> visited)
    {
        if (Same(current, target)) return true;
        if (!visited.Add(current) || !_waitingFor.TryGetValue(current, out var next)) return false;
        return next.Any(owner => Reaches(owner, target, visited));
    }
    private sealed record Reservation(string TaskId, HashSet<string> Resources,
        IReadOnlyList<string> PointCodes, IReadOnlyList<string> LineCodes, int RoutePointOffset, int RouteVersion);

    public sealed class Lease : IAlgorithmRouteLease
    {
        private readonly MultiVehicleTrafficCoordinator _owner;
        private bool _committed;
        private bool _disposed;

        internal Lease(MultiVehicleTrafficCoordinator owner, string vehicleId, string taskId,
            HashSet<string> resources, HashSet<string> previousResources,
            IReadOnlyList<string> pointCodes, IReadOnlyList<string> lineCodes,
            int routePointOffset, int routeVersion)
        {
            _owner = owner; VehicleId = vehicleId; TaskId = taskId;
            Resources = resources; PreviousResources = previousResources;
            PointCodes = pointCodes.ToArray(); LineCodes = lineCodes.ToArray();
            RoutePointOffset = routePointOffset; RouteVersion = routeVersion;
        }

        internal string VehicleId { get; }
        internal string TaskId { get; }
        internal HashSet<string> Resources { get; }
        internal HashSet<string> PreviousResources { get; }
        internal IReadOnlyList<string> PointCodes { get; }
        internal IReadOnlyList<string> LineCodes { get; }
        internal int RoutePointOffset { get; }
        internal int RouteVersion { get; }

        public void Commit()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Lease));
            if (_committed) return;
            _owner.Commit(this);
            _committed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_committed) _owner.Rollback(this);
        }
    }
}
