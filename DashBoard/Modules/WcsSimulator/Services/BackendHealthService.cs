namespace Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 后端存活状态共享服务（scoped = 每个浏览器标签页一个实例，纯状态持有者）。
///
/// 健康探测统一由 AutomationHub 常驻轮询（MainLayout 注入，每 1s 一轮）喂入：
/// WCS 后端经 /api/wcs/status、GRCS 后端经 /api/wcs/grcs/health 代理探测。
/// BackendStatus 组件渲染它，各页面读它判断「后端是否连接」，单一数据源，无重复探测。
/// </summary>
public class BackendHealthService
{
    private readonly object _lock = new();

    /// <summary>WCS 后端（8230）是否在线；null = 尚未上报。</summary>
    public bool? WcsOnline { get; private set; }

    /// <summary>GRCS 后端（8224，经 WCS 代理探测）是否在线；null = 尚未上报。</summary>
    public bool? GrcsOnline { get; private set; }

    /// <summary>状态变化时触发（值翻转才触发，不按轮询周期空转）。</summary>
    public event Action? Changed;

    /// <summary>AutomationHub 每 1s 回报 WCS 后端状态。</summary>
    public void ReportWcs(bool online)
    {
        lock (_lock)
        {
            if (WcsOnline == online) return;
            WcsOnline = online;
        }
        Changed?.Invoke();
    }

    /// <summary>AutomationHub 每 1s 回报 GRCS 后端状态（经 /api/wcs/grcs/health 代理探测）。</summary>
    public void ReportGrcs(bool online)
    {
        lock (_lock)
        {
            if (GrcsOnline == online) return;
            GrcsOnline = online;
        }
        Changed?.Invoke();
    }
}