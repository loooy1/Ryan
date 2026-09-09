namespace Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 后端连接告警弹窗服务（scoped，每个标签页独立）。
/// WcsApiClient 在检测到 WCS/GRCS 后端未连接且用户正在请求数据时调用 Show()，
/// Layout 中的 ConnectionAlert 组件订阅 Shown 事件渲染深色弹窗。
/// 相同消息不重复触发（后端离线期间弹窗保持打开，恢复在线后 Dismiss 关闭，再离线可再次弹出）。
/// </summary>
public class ConnectionAlertService
{
    /// <summary>消息变化时触发（非空 = 显示，空 = 关闭）。</summary>
    public event Action<string>? Shown;

    /// <summary>当前显示的消息；null/空 = 无告警。</summary>
    public string? Current { get; private set; }

    public void Show(string message)
    {
        if (Current == message) return;
        Current = message;
        Shown?.Invoke(message);
    }

    public void Dismiss()
    {
        if (Current == null) return;
        Current = null;
        Shown?.Invoke("");
    }
}