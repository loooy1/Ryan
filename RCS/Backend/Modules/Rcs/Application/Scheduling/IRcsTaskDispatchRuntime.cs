namespace RCSBackend.Modules.Rcs.Application.Scheduling;

/// <summary>仅供调度宿主使用的任务领取、恢复和执行观察入口。</summary>
public interface IRcsTaskDispatchRuntime
{
    void SetExecutionLifetime(CancellationToken token);
    Task RecoverAsync(CancellationToken token);
    Task WaitForWorkAsync(CancellationToken token);
    Task<bool> DispatchNextAsync(CancellationToken stoppingToken);
    Task ObserveExecutionsAsync(bool drain = false);
}
