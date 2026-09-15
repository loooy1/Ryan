using Contracts.Dtos;
using Contracts.Entities;
using Dashboard.Modules.WcsSimulator.Models;
using Microsoft.JSInterop;

namespace Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 任务记录共享服务（scoped = 每个浏览器标签页一个实例）。
/// 通过 SignalR 长连接（WCS 后端 /hubs/task-stages）实时接收合并表（task_records）全量记录，
/// 取代旧版的 HTTP 轮询（/api/wcs/task-stages?sinceId=N）与台账 HTTP 拉取。
///
/// ── 数据流 ──
/// 1. 连接建立（EnsureStartedAsync，MainLayout 注入调用）→ 后端回放全表快照（EventsReset）；
/// 2. 后端每写入一条记录（创建行 CREATED 或阶段行 START/LOAD_FINISH/FINISHED）即广播 EventAdded
///    → 本服务合并进缓存并触发 Changed；
/// 3. 其它标签页删除任务/清空 → 广播 TaskRemoved / EventsReset(空) → 同步本地缓存。
/// 4. 断线自动重连（JS 端 withAutomaticReconnect），重连成功后再收 EventsReset 对账。
///
/// 消费方全部零 HTTP：任务看板（创建行骨架 + 阶段时间线）、信号交互页（到达/移除/分拣卡片）、
/// 两段式任务等待（WaitFinishedAsync）。创建行也走本缓存，前端不再单独 HTTP 拉台账。
/// 连接状态用 IsOnline 暴露（true = 实时推送可用，与后端存活判定无关，后端存活判定走 BackendHealthService）。
/// </summary>
public class TaskStageHub : IDisposable
{
    private const string WcsUrlKey = "grcs_wcs_url";
    private const string DefaultWcsUrl = "http://localhost:8230";
    private const int MaxCache = 10000;         // 前端缓存上限（与后端 task_records 表上限一致）

    private readonly IJSRuntime _js;
    private readonly LocalStoreService _store;
    private readonly object _lock = new();
    private DotNetObjectReference<TaskStageHub>? _ref;
    private bool _started;
    private List<TaskRecord> _records = [];
    private readonly HashSet<string> _finished = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>阶段视图（过滤 CREATED 创建行，按到达顺序）。</summary>
    public IReadOnlyList<StageChangeEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _records.Where(r => !r.IsCreated).Select(r => new StageChangeEvent
                {
                    Id = r.Id,
                    TaskId = r.TaskId,
                    TaskType = r.TaskType,
                    Warehouse = r.Warehouse,
                    StartStationCode = r.StartStationCode,
                    EndStationCode = r.EndStationCode,
                    ContainerCode = r.ContainerCode,
                    Stage = r.Stage,
                    Time = r.Time,
                }).ToList();
            }
        }
    }

    /// <summary>
    /// task_records 完整视图，包含 CREATED 创建行和所有后续阶段。
    /// 任务看板使用此视图展示完整任务周期；返回副本，避免调用方修改共享缓存。
    /// </summary>
    public IReadOnlyList<TaskRecord> TaskRecords
    {
        get
        {
            lock (_lock)
            {
                return _records.OrderBy(record => record.Id).ToList();
            }
        }
    }

    /// <summary>已 WCS 任务完成的任务号集合（大小写不敏感）。</summary>
    public HashSet<string> FinishedTaskIds => _finished;

    /// <summary>纯移动任务循环状态（后端 MoveLoopRunner SignalR 广播，本标签页实时快照）。</summary>
    public MoveTaskStatsDto? MoveStats { get; private set; }

    /// <summary>纯移动任务循环状态变化时触发（MoveLoopService 订阅后转发给页面/状态栏）。</summary>
    public event Action? MoveStatsChanged;

    /// <summary>归巢模式状态（后端 NestRunner SignalR 广播，本标签页实时快照）。</summary>
    public NestStatsDto? NestStats { get; private set; }

    /// <summary>归巢模式状态变化时触发。</summary>
    public event Action? NestStatsChanged;

    /// <summary>新记录合并/状态变化时触发（订阅者据此刷新 UI / 唤醒等待者）。</summary>
    public event Action? Changed;

    // ── 请求信号（Mock 审批事件，后端 MockApprovalService 每次变更广播全量快照）──
    private const int MaxMockEvents = 500;
    private List<MockRequestEventDto> _mockEvents = [];

    /// <summary>请求信号事件缓存（后端快照，最新在前）。</summary>
    public IReadOnlyList<MockRequestEventDto> MockApprovalEvents
    {
        get { lock (_lock) return _mockEvents.ToList(); }
    }

    /// <summary>请求信号事件变化时触发（信号交互页订阅后读缓存渲染）。</summary>
    public event Action? MockApprovalEventsChanged;

    // ── 模块执行记录（后端 ModuleExecLogStore 增量单条 + 连接回放全量）──
    private const int MaxExecLog = 200;
    private List<ModuleExecLogEntry> _execLog = [];

    /// <summary>模块执行记录缓存（最新在前，上限 200 条）。</summary>
    public IReadOnlyList<ModuleExecLogEntry> ExecLogEntries
    {
        get { lock (_lock) return _execLog.ToList(); }
    }

    public event Action? ExecLogChanged;

    public TaskStageHub(IJSRuntime js, LocalStoreService store)
    {
        _js = js;
        _store = store;
    }

    /// <summary>建立 SignalR 连接（幂等；MainLayout 注入时调用，保证每个标签页常驻）。</summary>
    public async Task EnsureStartedAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            _ref = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("grcsTaskStage.setRef", _ref);
            await _js.InvokeVoidAsync("grcsTaskStage.connect", ResolveHubUrl());
        }
        catch (Exception ex)
        {
            try { await _js.InvokeVoidAsync("console.error", $"[TaskStageHub] 启动失败: {ex.Message}"); } catch { }
        }
    }

    /// <summary>请求后端从数据库读取全表快照并覆盖当前浏览器显示缓存。</summary>
    public async Task RefreshSnapshotAsync()
    {
        await EnsureStartedAsync();
        try { await _js.InvokeVoidAsync("grcsTaskStage.refreshSnapshot"); } catch { }
    }

    private string ResolveHubUrl()
    {
        var url = _store[WcsUrlKey];
        var baseUrl = string.IsNullOrEmpty(url) || url == "null" ? DefaultWcsUrl : url;
        return baseUrl.TrimEnd('/') + "/hubs/task-stages";
    }

    /// <summary>后端广播：单条新记录（创建行或阶段行）。</summary>
    [JSInvokable]
    public void OnEventAdded(TaskRecord evt)
    {
        if (evt == null || string.IsNullOrEmpty(evt.TaskId)) return;
        lock (_lock)
        {
            _records.Add(evt);
            if (_records.Count > MaxCache) _records.RemoveRange(0, _records.Count - MaxCache);
            if (string.Equals(evt.Stage, "WCS_COMPLETED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(evt.StageStatus, TaskStageStatuses.Success, StringComparison.OrdinalIgnoreCase))
                _finished.Add(evt.TaskId);
        }
        Changed?.Invoke();
    }

    /// <summary>后端广播：全表快照（连接建立/清空后对账）→ 整表替换。</summary>
    [JSInvokable]
    public void OnEventsReset(List<TaskRecord>? records)
    {
        lock (_lock)
        {
            _records = records ?? [];
            if (_records.Count > MaxCache) _records.RemoveRange(0, _records.Count - MaxCache);
            _finished.Clear();
            foreach (var r in _records)
                if (string.Equals(r.Stage, "WCS_COMPLETED", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.StageStatus, TaskStageStatuses.Success, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(r.TaskId))
                    _finished.Add(r.TaskId);
        }
        Changed?.Invoke();
    }

    /// <summary>后端广播：某任务被删除（其它标签页操作）→ 同步本地缓存。</summary>
    [JSInvokable]
    public void OnTaskRemoved(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        RemoveTask(taskId);
    }

    /// <summary>后端广播：纯移动任务循环状态（每轮统计/启停/错误）。</summary>
    [JSInvokable]
    public void OnMoveTaskStats(MoveTaskStatsDto dto)
    {
        if (dto == null) return;
        MoveStats = dto;
        MoveStatsChanged?.Invoke();
    }

    /// <summary>后端广播：归巢模式状态（执行中/就绪车/统计/错误）。</summary>
    [JSInvokable]
    public void OnNestStats(NestStatsDto dto)
    {
        if (dto == null) return;
        NestStats = dto;
        NestStatsChanged?.Invoke();
    }

    /// <summary>后端广播：请求信号记录全量快照（每次变更/连接建立回放）→ 整表替换（天然无重复）。</summary>
    [JSInvokable]
    public void OnMockRequestEvents(List<MockRequestEventDto>? events)
    {
        lock (_lock)
        {
            _mockEvents = events ?? [];
            if (_mockEvents.Count > MaxMockEvents) _mockEvents.RemoveRange(0, _mockEvents.Count - MaxMockEvents);
        }
        MockApprovalEventsChanged?.Invoke();
    }

    /// <summary>后端广播：模块执行记录全量回放（连接建立/重连对账）→ 整表替换，清除重复残留。</summary>
    [JSInvokable]
    public void OnModuleExecLogsReset(ExecLogsResetPayload? payload)
    {
        if (payload == null) return;
        lock (_lock)
        {
            _execLog = payload.Entries ?? [];
            if (_execLog.Count > MaxExecLog) _execLog.RemoveRange(0, _execLog.Count - MaxExecLog);
        }
        ExecLogChanged?.Invoke();
    }

    /// <summary>后端广播：单条新模块执行记录（插头部；按 Id 判重，防迟到/重复推送）。</summary>
    [JSInvokable]
    public void OnModuleExecLogAdded(ModuleExecLogEntry entry)
    {
        if (entry == null) return;
        lock (_lock)
        {
            if (_execLog.Any(x => x.Id == entry.Id)) return;
            _execLog.Insert(0, entry);
            if (_execLog.Count > MaxExecLog) _execLog.RemoveRange(MaxExecLog, _execLog.Count - MaxExecLog);
        }
        ExecLogChanged?.Invoke();
    }

    /// <summary>后端 ModuleExecLogsReset 回放负载（{maxId, entries}，JS 侧 camelCase → 本端大小写不敏感匹配）。</summary>
    public class ExecLogsResetPayload
    {
        public long MaxId { get; set; }
        public List<ModuleExecLogEntry> Entries { get; set; } = [];
    }

    /// <summary>JS 回报连接状态：connected / reconnecting / disconnected。</summary>
    [JSInvokable]
    public void OnStateChanged(string state)
    {
        Changed?.Invoke();
    }

    /// <summary>删除某任务后调用：同步清掉本地缓存（全行：创建 + 阶段），防止等待者基于陈旧数据误判。</summary>
    public void RemoveTask(string taskId)
    {
        lock (_lock)
        {
            _records.RemoveAll(r => string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            _finished.Remove(taskId);
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        try { _js.InvokeVoidAsync("grcsTaskStage.disconnect"); } catch { }
        _ref?.Dispose();
        _ref = null;
    }
}
