namespace RCSBackend.Modules.Rcs.Application.Execution;

/// <summary>一台车当前任务的路径窗口和站点放行状态；由执行服务独占管理。</summary>
public sealed class RcsTaskExecutionSession(RcsTaskExecutionPlan plan, string commandId, CancellationToken token)
{
    public RcsTaskExecutionPlan Plan { get; set; } = plan;
    public string CommandId { get; set; } = commandId;
    public CancellationToken Token { get; } = token;
    public SemaphoreSlim WindowGate { get; } = new(1, 1);
    public int SegmentIndex { get; set; }
    public int RouteVersion { get; set; } = plan.RouteVersion;
    public bool HasAcceptedCommand { get; set; }
    public Exception? RouteUpdateError { get; set; }
    public HashSet<int> AuthorizedEntryIndices { get; } = [];
    public HashSet<int> CheckedEntryIndices { get; } = [];
}
