namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// 自动化互斥闸（进程内硬互斥 + 跨标签页协调）：
/// 轮询自动化（AutoTemplateRunner）、单次执行（AutoTemplateRunner.ExecuteOnce）、
/// 纯移动任务循环（MoveLoopRunner，后端循环）共享同一批车辆/储位/接驳位资源，
/// 必须互斥运行——任意一个在执行时，其他一律拒绝启动，杜绝并发下发。
/// 纯移动循环由后端 MoveLoopRunner 进程内管理：Start 登记、Stop 释放，
/// 进程退出时闸为内存态自然失效，无需心跳/TTL。
/// 三个模式均记录发起者 tabId，供前端区分「本页启动」vs「其他标签页启动」。
/// </summary>
public class AutomationGate
{
    private readonly object _lock = new();

    public bool AutoRunning { get; private set; }

    /// <summary>各模式发起者标签页 id（空 = 老版本启动，无主）。</summary>
    public string? AutoTabId { get; private set; }
    public string? MoveTabId { get; private set; }

    /// <summary>纯移动循环运行中（MoveLoopRunner 登记，停止时释放）。</summary>
    public bool MoveRunning => MoveTabId != null;

    /// <summary>是否任一模式下发中（前端警示/禁用判断用）。</summary>
    public bool AnyRunning => AutoRunning || MoveRunning;

    /// <summary>尝试启动轮询自动化（移动循环运行中则拒绝）。</summary>
    public bool TryStartAuto(string? tabId)
    {
        lock (_lock)
        {
            if (MoveRunning) return false;
            AutoRunning = true;
            AutoTabId = tabId;
            return true;
        }
    }

    public void StopAuto()
    {
        lock (_lock) { AutoRunning = false; AutoTabId = null; }
    }

    /// <summary>尝试登记移动任务循环（轮询运行中，或另一活跃属主持有则拒绝）。</summary>
    public (bool Ok, string? Reason) TryStartMove(string tabId)
    {
        lock (_lock)
        {
            if (AutoRunning) return (false, "轮询自动化正在运行中");
            if (MoveRunning && MoveTabId != tabId) return (false, "另一标签页正在循环下发移动任务");
            MoveTabId = tabId;
            return (true, null);
        }
    }

    /// <summary>释放移动循环租约（停止时调用）。</summary>
    public void StopMove(string tabId)
    {
        lock (_lock) { if (MoveTabId == tabId) MoveTabId = null; }
    }
}