namespace RCSBackend.Modules.Rcs.Application.Scheduling;

/// <summary>协调任务状态、车辆增删及控制；长时间车辆运行不占用此锁。</summary>
public sealed class RcsDispatchLock : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public void Dispose() => Gate.Dispose();
}
