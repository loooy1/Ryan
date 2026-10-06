using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Contracts.Rcs.Protocol;
using Contracts.Rcs.Vehicle;

namespace Rcs.VirtualVehicle;

/// <summary>协议接收端。每个到达点执行动作；路径更新保留任务、完成信号和已完成步骤。</summary>
public sealed class VirtualVehicleSimulator(string vehicleId = "V-01") : IVirtualVehicle
{
    private const int HistoryLimit = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private readonly Dictionary<string, VehicleRoutePoint> _completedSteps = new(StringComparer.Ordinal);
    private Run? _active;
    private VehicleRoutePoint? _point;
    private string _taskId = "", _commandId = "", _cargo = "", _lastAction = VehiclePointAction.Move, _lockPointCode = "";
    private string _currentPointAction = VehiclePointAction.Move, _currentPointStepId = "";
    private string _status = "Idle";
    private int _routeIndex, _routeLength, _routeVersion, _routeOffset, _totalRouteLength;

    public event Action<VehicleStateDto>? StateChanged;
    public VehicleStateDto State
    {
        get
        {
            lock (_gate)
                return new VehicleStateDto { Id = vehicleId, TaskId = _taskId, CommandId = _commandId,
                    RouteVersion = _routeVersion, LoadedContainerCode = _cargo, LastAction = _lastAction,
                    CompletedStepIds = _completedSteps.Keys.ToArray(), PointCode = _point?.PointCode ?? "",
                    LockPointCode = _lockPointCode,
                    X = _point?.X ?? 0, Y = _point?.Y ?? 0, Z = _point?.Z ?? 0,
                    Status = _status, RouteIndex = _routeOffset + _routeIndex,
                    RouteLength = _totalRouteLength == 0 ? _routeLength : _totalRouteLength };
        }
    }

    public VehicleCommandAck Receive(VehicleCommand command)
    {
        // 保留独立报文副本，调用方后续修改数组不能改变已确认的路径。
        var payload = JsonSerializer.Serialize(command);
        command = JsonSerializer.Deserialize<VehicleCommand>(payload)!;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        Run? start = null;
        VehicleCommandAck ack;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 256)
                return new(command.CommandId, false, "CommandId 不能为空且最多 256 字符。");
            if (_receipts.TryGetValue(command.CommandId, out var cached))
                return cached.Fingerprint == fingerprint ? cached.Ack : new(command.CommandId, false, "CommandId 已存在且内容不同。");
            var receipt = new Receipt(fingerprint);
            try
            {
                if (!string.Equals(command.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("车辆不存在。");
                switch (command.Type)
                {
                    case VehicleCommandType.Move:
                        ValidateRoute(command);
                        if (_active is not null) throw new InvalidOperationException("车辆忙碌，不能用新 MOVE 覆盖当前任务。");
                        var continuingTask = string.Equals(_taskId, command.TaskId, StringComparison.OrdinalIgnoreCase);
                        start = _active = new Run(command, receipt.Completion);
                        _taskId = command.TaskId; _commandId = command.CommandId;
                        _routeVersion = command.RouteVersion; _routeOffset = command.RoutePointOffset;
                        _routeLength = command.Points.Count; _routeIndex = 0;
                        _totalRouteLength = command.TotalRoutePoints == 0 ? command.Points.Count : command.TotalRoutePoints;
                        _lockPointCode = command.Points[^1].PointCode;
                        if (!continuingTask) _completedSteps.Clear();
                        _status = command.StartPaused ? "Paused" : "Running";
                        break;
                    case VehicleCommandType.UpdateRoute:
                        ValidateRoute(command);
                        var run = RequireRun(command.TaskId);
                        if (!run.Paused) throw new InvalidOperationException("请先暂停车辆并确认位置，再更新路径。");
                        if (command.RouteVersion <= _routeVersion) throw new ArgumentException("路径版本必须递增。");
                        if (command.ExpectedPointCode != (_point?.PointCode ?? "")
                            || command.Points[0].PointCode != command.ExpectedPointCode
                            || command.Points[0].Action != VehiclePointAction.Move || command.Points[0].StepId != "")
                            throw new ArgumentException("新路径必须从确认的当前位置开始，首点仅移动。");
                        if (command.ContainerCode != run.Command.ContainerCode) throw new ArgumentException("改路不能更换容器。");
                        foreach (var point in command.Points.Where(x => x.StepId != ""))
                            if (_completedSteps.TryGetValue(point.StepId, out var done)
                                && (done.PointCode != point.PointCode || done.Action != point.Action))
                                throw new ArgumentException("已完成步骤不能更换站点或动作。");
                        run.Points = command.Points; run.NextIndex = 0; run.HasMorePoints = command.HasMorePoints;
                        run.RouteWaitStartedAt = null;
                        _routeVersion = command.RouteVersion; _routeOffset = command.RoutePointOffset;
                        _routeLength = command.Points.Count; _routeIndex = 0;
                        _totalRouteLength = command.TotalRoutePoints == 0 ? command.Points.Count : command.TotalRoutePoints;
                        _lockPointCode = command.Points[^1].PointCode;
                        break;
                    case VehicleCommandType.ExecuteAction:
                        if (_active is not null) throw new InvalidOperationException("车辆仍在移动，不能执行站点动作。");
                        if (command.TaskId != _taskId) throw new InvalidOperationException("动作任务与车辆当前任务不一致。");
                        if (_point is null || command.ExpectedPointCode != _point.PointCode)
                            throw new InvalidOperationException("车辆未到达动作指定站点。");
                        if (command.Action is not (VehiclePointAction.Fetch or VehiclePointAction.Put)
                            || string.IsNullOrWhiteSpace(command.ActionStepId) || string.IsNullOrWhiteSpace(command.ContainerCode))
                            throw new ArgumentException("独立动作命令必须包含 fetch/put、步骤编号和容器编码。");
                        if (_completedSteps.TryGetValue(command.ActionStepId, out var completedAction))
                        {
                            if (completedAction.PointCode != _point.PointCode || completedAction.Action != command.Action)
                                throw new InvalidOperationException("动作步骤已完成，但站点或动作与重复命令不一致。");
                        }
                        else
                        {
                            if (command.Action == VehiclePointAction.Fetch)
                            {
                                if (_cargo != "") throw new InvalidOperationException($"车上已有容器 {_cargo}，不能再次取货。");
                                _cargo = command.ContainerCode;
                            }
                            else
                            {
                                if (_cargo != command.ContainerCode) throw new InvalidOperationException("车上容器与放货要求不一致。");
                                _cargo = "";
                            }
                            _currentPointAction = _lastAction = command.Action;
                            _currentPointStepId = command.ActionStepId;
                            _completedSteps.Add(command.ActionStepId, new VehicleRoutePoint
                            {
                                PointCode = _point.PointCode, X = _point.X, Y = _point.Y, Z = _point.Z,
                                Action = command.Action, StepId = command.ActionStepId
                            });
                        }
                        _commandId = command.CommandId;
                        _status = "Arrived";
                        break;
                    case VehicleCommandType.SlideRoute:
                        ValidateRoute(command);
                        var slidingRun = RequireRun(command.TaskId);
                        if (slidingRun.Paused) throw new InvalidOperationException("车辆已暂停，不能续发路径。");
                        if (command.RouteVersion != _routeVersion + 1) throw new ArgumentException("滑动路径版本必须连续递增。");
                        if (command.ContainerCode != slidingRun.Command.ContainerCode) throw new ArgumentException("续发路径不能更换容器。");
                        var expectedGlobalIndex = _routeOffset + _routeIndex - 1;
                        if (command.RoutePointOffset != expectedGlobalIndex
                            || command.ExpectedPointCode != (_point?.PointCode ?? "")
                            || command.Points[0].PointCode != command.ExpectedPointCode
                            || command.Points[0].X != _point!.X || command.Points[0].Y != _point.Y
                            || command.Points[0].Z != _point.Z
                            || command.Points[0].Action != _currentPointAction
                            || command.Points[0].StepId != _currentPointStepId)
                            throw new ArgumentException("续发路径必须从车辆当前已确认的点及其动作开始。");
                        slidingRun.Points = command.Points; slidingRun.NextIndex = 1;
                        slidingRun.HasMorePoints = command.HasMorePoints; slidingRun.RouteWaitStartedAt = null;
                        _routeVersion = command.RouteVersion; _routeOffset = command.RoutePointOffset;
                        _routeLength = command.Points.Count; _routeIndex = 1;
                        _totalRouteLength = command.TotalRoutePoints == 0 ? command.Points.Count : command.TotalRoutePoints;
                        _lockPointCode = command.Points[^1].PointCode;
                        break;
                    case VehicleCommandType.Pause:
                        RequireRun(command.TaskId).Paused = true; _status = "Paused";
                        break;
                    case VehicleCommandType.Resume:
                        RequireRun(command.TaskId).Paused = false; _status = "Running";
                        break;
                    case VehicleCommandType.Stop:
                        if (_active is not null) { RequireRun(command.TaskId); Finish(_active, "STOPPED", "车辆已停止。"); }
                        break;
                    case VehicleCommandType.Reset:
                        if (command.ResetPoint is { } reset) ValidatePoint(reset);
                        if (_active is not null) Finish(_active, "STOPPED", "车辆已重置。");
                        _point = command.ResetPoint; _taskId = ""; _commandId = "";
                        _routeVersion = _routeIndex = _routeLength = _routeOffset = _totalRouteLength = 0; _completedSteps.Clear(); _status = "Idle";
                        _lockPointCode = "";
                        _lastAction = _currentPointAction = VehiclePointAction.Move; _currentPointStepId = "";
                        // 重置位置不等于卸货；保留车上容器，避免取消/重置造成货物凭空消失。
                        break;
                    default: throw new ArgumentException($"不支持命令 {command.Type}。");
                }
                ack = new(command.CommandId, true, "命令已接收。");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { ack = new(command.CommandId, false, ex.Message); }
            receipt.Ack = ack;
            if (start is null) receipt.Completion.TrySetResult(new(command.CommandId, command.TaskId,
                command.RouteVersion, ack.Accepted ? "COMPLETED" : "FAILED", ack.Message));
            _receipts.Add(command.CommandId, receipt); _receiptOrder.Enqueue(command.CommandId);
            TrimHistory();
        }
        Publish();
        if (start is not null) _ = Task.Run(() => RunAsync(start));
        return ack;
    }

    public Task<VehicleCommandResult> WaitForCompletionAsync(string commandId, CancellationToken token = default)
    {
        lock (_gate)
            return _receipts.TryGetValue(commandId, out var receipt) ? receipt.Completion.Task.WaitAsync(token)
                : Task.FromException<VehicleCommandResult>(new ArgumentException("命令不存在或已超出保留范围。"));
    }

    private async Task RunAsync(Run run)
    {
        try
        {
            while (true)
            {
                var moved = false;
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, run)) return;
                    if (!run.Paused)
                    {
                        if (run.NextIndex == run.Points.Count)
                        {
                            if (!run.HasMorePoints) Finish(run, "COMPLETED", "路径及点位动作执行完成。");
                            else if (run.RouteWaitStartedAt is null) run.RouteWaitStartedAt = DateTimeOffset.UtcNow;
                        }
                        else
                        {
                            var point = run.Points[run.NextIndex];
                            _point = point; _routeIndex = ++run.NextIndex;
                            ApplyAction(run, point); moved = true;
                        }
                    }
                }
                if (moved || run.Completion.Task.IsCompleted) Publish();
                if (run.Completion.Task.IsCompleted) return;
                await Task.Delay(moved ? 300 : 50);
            }
        }
        catch (Exception ex)
        {
            lock (_gate) { if (ReferenceEquals(_active, run)) Finish(run, "FAILED", ex.Message); }
            Publish();
        }
    }

    private void ApplyAction(Run run, VehicleRoutePoint point)
    {
        _currentPointAction = point.Action; _currentPointStepId = point.StepId;
        if (point.StepId != "" && _completedSteps.ContainsKey(point.StepId)) return;
        switch (point.Action)
        {
            case VehiclePointAction.Fetch:
                if (_cargo != "") throw new InvalidOperationException($"车上已有容器 {_cargo}，不能再次取货。");
                _cargo = run.Command.ContainerCode; break;
            case VehiclePointAction.Put:
                if (_cargo != run.Command.ContainerCode) throw new InvalidOperationException("车上容器与放货要求不一致。");
                _cargo = ""; break;
        }
        _lastAction = point.Action;
        if (point.StepId != "") _completedSteps.Add(point.StepId, point);
    }

    private void Finish(Run run, string status, string message)
    {
        _active = null; _lockPointCode = ""; _status = status == "COMPLETED" ? "Arrived" : "Idle";
        run.Completion.TrySetResult(new(run.Command.CommandId, run.Command.TaskId, _routeVersion, status, message));
    }

    private Run RequireRun(string taskId) => _active is { } run && run.Command.TaskId == taskId ? run
        : throw new InvalidOperationException("任务与车辆当前执行任务不一致或任务已经结束。");

    private static void ValidateRoute(VehicleCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.TaskId) || command.RouteVersion < 1)
            throw new ArgumentException("TaskId 不能为空，RouteVersion 必须大于 0。");
        if (command.Points is null || command.Points.Count is < 1 or > 100000)
            throw new ArgumentException("路径必须包含 1～100000 个点。");
        var totalPoints = command.TotalRoutePoints == 0 ? command.Points.Count : command.TotalRoutePoints;
        if (command.RoutePointOffset < 0 || totalPoints < 1
            || command.RoutePointOffset + command.Points.Count > totalPoints
            || command.HasMorePoints != (command.RoutePointOffset + command.Points.Count < totalPoints))
            throw new ArgumentException("路径窗口偏移、总长度或续发标记不匹配。");
        var steps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in command.Points)
        {
            ValidatePoint(point);
            if (point.StepId != "" && !steps.Add(point.StepId)) throw new ArgumentException("路径中 StepId 不能重复。");
            if (point.Action != VehiclePointAction.Move
                && (string.IsNullOrWhiteSpace(point.StepId) || string.IsNullOrWhiteSpace(command.ContainerCode)))
                throw new ArgumentException("取放货点必须有 StepId 和 ContainerCode。");
        }
    }

    private static void ValidatePoint(VehicleRoutePoint point)
    {
        if (point is null || string.IsNullOrWhiteSpace(point.PointCode)
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)
            || point.StepId is null || point.Action is not (VehiclePointAction.Move or VehiclePointAction.Fetch or VehiclePointAction.Put))
            throw new ArgumentException("路径点编码、坐标或动作非法。");
    }

    private void TrimHistory()
    {
        while (_receipts.Count > HistoryLimit)
        {
            var id = _receiptOrder.Dequeue();
            if (id == _active?.Command.CommandId) _receiptOrder.Enqueue(id);
            else _receipts.Remove(id);
        }
    }

    private void Publish() => StateChanged?.Invoke(State);
    private sealed class Receipt(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public VehicleCommandAck Ack { get; set; } = null!;
        public TaskCompletionSource<VehicleCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Run(VehicleCommand command, TaskCompletionSource<VehicleCommandResult> completion)
    {
        public VehicleCommand Command { get; } = command;
        public TaskCompletionSource<VehicleCommandResult> Completion { get; } = completion;
        public IReadOnlyList<VehicleRoutePoint> Points { get; set; } = command.Points;
        public int NextIndex { get; set; }
        public bool Paused { get; set; } = command.StartPaused;
        public bool HasMorePoints { get; set; } = command.HasMorePoints;
        public DateTimeOffset? RouteWaitStartedAt { get; set; }
    }
}
