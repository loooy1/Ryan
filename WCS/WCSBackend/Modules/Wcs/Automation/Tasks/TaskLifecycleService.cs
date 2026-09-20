using Contracts.Entities;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>WCS 进程内任务生命周期。原始阶段事件仍由 TaskStageService 持久化。</summary>
public enum WcsTaskState
{
    None,
    Created,
    Dispatching,
    Accepted,
    Running,
    LoadFinished,
    Finished,
    DispatchFailed,
    Unknown,
    Cancelled,
}

public readonly record struct TaskStateTransition(
    bool Changed,
    WcsTaskState Previous,
    WcsTaskState Current,
    string? Reason = null);

/// <summary>
/// 集中校验任务状态转换。重复和迟到事件保留在事件流水中，但不会再次触发业务副作用。
/// 当前不负责重启恢复任务执行，也不自动重试 GRCS 请求。
/// </summary>
public sealed class TaskLifecycleService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, WcsTaskState> _states = new(StringComparer.OrdinalIgnoreCase);

    public TaskStateTransition BeginDispatch(string taskId)
        => Apply(taskId, WcsTaskState.Dispatching);

    public TaskStateTransition DispatchAccepted(string taskId)
        => Apply(taskId, WcsTaskState.Accepted);

    public TaskStateTransition DispatchFailed(string taskId, bool resultUnknown)
        => Apply(taskId, resultUnknown ? WcsTaskState.Unknown : WcsTaskState.DispatchFailed);

    public TaskStateTransition Cancel(string taskId)
        => Apply(taskId, WcsTaskState.Cancelled);

    public TaskStateTransition ApplyStage(string taskId, string stage)
    {
        var next = NormalizeStage(stage);
        return next == WcsTaskState.None
            ? new TaskStateTransition(false, GetState(taskId), GetState(taskId), $"未知阶段: {stage}")
            : Apply(taskId, next);
    }

    public WcsTaskState GetState(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return WcsTaskState.None;
        lock (_lock) return _states.GetValueOrDefault(taskId, WcsTaskState.None);
    }

    public void Seed(IEnumerable<TaskRecord> records)
    {
        foreach (var record in records.OrderBy(x => x.Id))
        {
            if (string.IsNullOrWhiteSpace(record.TaskId)) continue;
            if (record.IsCreated) Apply(record.TaskId, WcsTaskState.Accepted);
            else ApplyStage(record.TaskId, record.Stage);
        }
    }

    public void Remove(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        lock (_lock) _states.Remove(taskId);
    }

    public void Clear()
    {
        lock (_lock) _states.Clear();
    }

    private TaskStateTransition Apply(string taskId, WcsTaskState next)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return new TaskStateTransition(false, WcsTaskState.None, WcsTaskState.None, "任务号为空");

        lock (_lock)
        {
            var previous = _states.GetValueOrDefault(taskId, WcsTaskState.None);
            if (previous == next)
                return new TaskStateTransition(false, previous, previous, "重复事件");
            if (!CanTransition(previous, next))
                return new TaskStateTransition(false, previous, previous, $"不允许 {previous} -> {next}");

            _states[taskId] = next;
            return new TaskStateTransition(true, previous, next);
        }
    }

    private static WcsTaskState NormalizeStage(string stage)
        => stage.Trim().ToUpperInvariant() switch
        {
            "CREATED" => WcsTaskState.Accepted,
            "START" => WcsTaskState.Running,
            "LOAD_FINISH" => WcsTaskState.LoadFinished,
            "FINISHED" => WcsTaskState.Finished,
            "CANCELLED" => WcsTaskState.Cancelled,
            _ => WcsTaskState.None,
        };

    private static bool CanTransition(WcsTaskState previous, WcsTaskState next)
        => previous switch
        {
            WcsTaskState.None => true,
            WcsTaskState.Created => next is WcsTaskState.Dispatching or WcsTaskState.Accepted
                or WcsTaskState.Running or WcsTaskState.LoadFinished or WcsTaskState.Finished
                or WcsTaskState.DispatchFailed or WcsTaskState.Unknown or WcsTaskState.Cancelled,
            WcsTaskState.Dispatching => next is WcsTaskState.Accepted or WcsTaskState.Running
                or WcsTaskState.LoadFinished or WcsTaskState.Finished or WcsTaskState.DispatchFailed
                or WcsTaskState.Unknown or WcsTaskState.Cancelled,
            WcsTaskState.Accepted => next is WcsTaskState.Running or WcsTaskState.LoadFinished
                or WcsTaskState.Finished or WcsTaskState.Unknown or WcsTaskState.Cancelled,
            WcsTaskState.Unknown => next is WcsTaskState.Accepted or WcsTaskState.Running
                or WcsTaskState.LoadFinished or WcsTaskState.Finished or WcsTaskState.DispatchFailed
                or WcsTaskState.Cancelled,
            WcsTaskState.DispatchFailed => next is WcsTaskState.Running or WcsTaskState.LoadFinished
                or WcsTaskState.Finished,
            WcsTaskState.Running => next is WcsTaskState.LoadFinished or WcsTaskState.Finished
                or WcsTaskState.Cancelled,
            WcsTaskState.LoadFinished => next is WcsTaskState.Finished or WcsTaskState.Cancelled,
            WcsTaskState.Finished or WcsTaskState.Cancelled => false,
            _ => false,
        };
}
