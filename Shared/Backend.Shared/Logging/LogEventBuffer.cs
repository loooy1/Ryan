namespace Backend.Shared.Logging;

public sealed class LogEventBuffer
{
    private readonly object _gate = new();
    private readonly Queue<AppLogEvent> _events = new();
    private readonly int _capacity;

    public LogEventBuffer(int capacity) => _capacity = Math.Max(100, capacity);

    public event Action<AppLogEvent>? EventAdded;

    internal void Add(AppLogEvent entry)
    {
        lock (_gate)
        {
            _events.Enqueue(entry);
            while (_events.Count > _capacity) _events.Dequeue();
        }

        var handlers = EventAdded;
        if (handlers == null) return;
        foreach (Action<AppLogEvent> handler in handlers.GetInvocationList())
        {
            try { handler(entry); }
            catch { /* 日志订阅者不能影响文件落盘。 */ }
        }
    }

    public IReadOnlyList<AppLogEvent> GetRecent(string? category = null, string? component = null,
        string? taskId = null, int take = 200)
    {
        take = Math.Clamp(take, 1, _capacity);
        lock (_gate)
        {
            return _events
                .Where(x => string.IsNullOrWhiteSpace(category) || x.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(component) || x.Component.Equals(component, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(taskId) || x.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase))
                .TakeLast(take)
                .ToList();
        }
    }
}
