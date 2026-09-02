using System.Text.Json;
using GrcsBackend.Modules.Shared.Infrastructure.Repository;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Contracts.Entities;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Realtime;
using Mapster;
using Microsoft.AspNetCore.SignalR;

namespace GrcsBackend.Modules.Wcs.Console.Services;

/// <summary>
/// 通用 Mock 审批服务：任意 Mock 卡片（RequiresApproval=true）命中时生成一条请求任务，
/// 前端在信号交互→请求信号中逐条批准/拒绝，审批结果控制 ResponseBody 中 ApprovalVariable 变量。
/// 每次变更（新增/归并/决策/删除/清空/放行换 Key）经 SignalR 广播全量 MockRequestEvents，
/// 前端信号交互页订阅后整表替换，零轮询。
/// </summary>
public class MockApprovalService
{
    private readonly object _lock = new();
    private long _seq;
    private readonly Dictionary<string, MockRequestEventDto> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUnitOfWorkFactory _uow;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;
    private const int MaxEvents = 500;

    public MockApprovalService(IUnitOfWorkFactory uowFactory, IHubContext<TaskStageRealtimeHub> hub)
    {
        _uow = uowFactory;
        _hub = hub;
        try
        {
            using var uow = _uow.Create();
            foreach (var row in uow.Repository<MockRequestEventRow>().FindAllAsync().GetAwaiter().GetResult())
            {
                var ev = row.Adapt<MockRequestEventDto>();
                _events[ev.Key] = ev;
                if (ev.Id > _seq) _seq = ev.Id;
            }
        }
        catch { }
    }

    private void Persist(MockRequestEventDto e)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<MockRequestEventRow>();
        var row = repo.FindAsync(e.Key).GetAwaiter().GetResult();
        if (row == null)
            repo.AddAsync(e.Adapt<MockRequestEventRow>()).GetAwaiter().GetResult();
        else
            e.Adapt(row);
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>裁剪：删除超限的已处理（非 Pending）记录，Pending 不受上限约束。</summary>
    private void TrimEvents(int max)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<MockRequestEventRow>();
        var keep = repo.Query().Where(x => x.Status != "Pending")
            .OrderByDescending(x => x.Time).ThenByDescending(x => x.Key)
            .Take(max).Select(x => x.Key).ToList();
        if (keep.Count > 0)
            repo.DeleteWhereAsync(x => x.Status != "Pending" && !keep.Contains(x.Key)).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private void RemoveEventDb(string key)
    {
        using var uow = _uow.Create();
        uow.Repository<MockRequestEventRow>().DeleteWhereAsync(x => x.Key == key).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private void ClearProcessedDb()
    {
        using var uow = _uow.Create();
        uow.Repository<MockRequestEventRow>().DeleteWhereAsync(x => x.Status != "Pending").GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public int PendingCount { get { lock (_lock) return _events.Values.Count(e => e.Status == "Pending"); } }

    public List<MockRequestEventDto> GetEvents()
    {
        lock (_lock) return _events.Values.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>广播全量事件快照（低频变更，整表推送，前端整表替换天然无重复）。</summary>
    private void BroadcastEvents()
    {
        var events = GetEvents();
        _ = _hub.Clients.All.SendAsync("MockRequestEvents", events);
    }

    /// <summary>命中需审批的 Mock 时调用：按 RuleId+BodyHash 生成 Key，首次 Pending，后续 Attempts++。
    /// 一次进站一审批：Approved（已批准未领取）重试时放行变 Allowed（车辆进站），
    /// 放行后卡片更换内部唯一标识（原 Key 退出匹配），同 Key 后续请求（下一次进站）生成全新卡片；
    /// Rejected 保持拒绝（同 Key 重试继续拒绝，用户可改批）。</summary>
    public (bool hasDecision, bool allow) TryConsumeDecision(MockRuleDto rule, string bodyJson, string queryString)
    {
        var key = ComputeKey(rule, bodyJson, queryString);
        lock (_lock)
        {
            // 已有事件：按状态决定放行/重新审批
            if (_events.TryGetValue(key, out var existing) && existing.Status != "Pending")
            {
                if (existing.Status == "Approved")
                {
                    // 放行：卡片变 Allowed 保留展示，但更换内部唯一标识退出匹配，
                    // 原 Key 的下一次进站请求将生成全新卡片（Attempts 从 1 重新计）
                    existing.Status = "Allowed";
                    existing.DecidedAt ??= DateTime.Now;
                    _decisions.Remove(key);
                    _events.Remove(key);
                    existing.Key = $"{key}#{existing.Id}";
                    _events[existing.Key] = existing;
                    RemoveEventDb(key);
                    Persist(existing);
                    BroadcastEvents();
                    return (true, true);
                }
                if (existing.Status == "Rejected") return (true, false);
            }
            else if (_events.TryGetValue(key, out var pending0) && pending0.Status == "Pending")
            {
                pending0.Attempts++;
                pending0.Time = DateTime.Now;
                pending0.BodyJson = bodyJson ?? "";      // 刷新为最新一次命中请求
                pending0.QueryString = queryString ?? "";
                Persist(pending0);
                BroadcastEvents();
                return (false, false);
            }

            if (_decisions.TryGetValue(key, out var allow))
            {
                if (_events.TryGetValue(key, out var ev))
                {
                    ev.Status = allow ? "Allowed" : "Rejected";
                    ev.DecidedAt = DateTime.Now;
                    Persist(ev);
                }
                BroadcastEvents();
                return (true, allow);
            }
            // 未审批则生成/累加 Pending 任务
            if (!_events.TryGetValue(key, out var pending))
            {
                _events[key] = new MockRequestEventDto
                {
                    Id = ++_seq,
                    Key = key,
                    PathPattern = rule.PathPattern,
                    Method = rule.Method,
                    BodyJson = bodyJson ?? "",
                    QueryString = queryString ?? "",
                    Time = DateTime.Now,
                    Status = "Pending",
                    Attempts = 1,
                    MockRuleId = rule.Id,
                    MockRuleDescription = rule.Description,
                    RuleJson = JsonSerializer.Serialize(rule)
                };
                Persist(_events[key]);
                TrimEvents(MaxEvents);
                BroadcastEvents();
            }
            else
            {
                pending.Attempts++;
                pending.Time = DateTime.Now;
                pending.BodyJson = bodyJson ?? "";      // 刷新为最新一次命中请求
                pending.QueryString = queryString ?? "";
                Persist(pending);
                BroadcastEvents();
            }
            return (false, false);
        }
    }

    /// <summary>命中无需审批的 Mock 时调用：同样生成一条 AutoPass 记录（同 Key 归并 Attempts++），供请求信号页查看。</summary>
    public void RecordAutoPass(MockRuleDto rule, string bodyJson, string queryString)
    {
        var key = ComputeKey(rule, bodyJson, queryString);
        lock (_lock)
        {
            if (_events.TryGetValue(key, out var ev))
            {
                ev.Attempts++;
                ev.Time = DateTime.Now;
                ev.BodyJson = bodyJson ?? "";
                ev.QueryString = queryString ?? "";
                ev.DecidedAt = DateTime.Now;
                Persist(ev);
                BroadcastEvents();
                return;
            }
            _events[key] = new MockRequestEventDto
            {
                Id = ++_seq,
                Key = key,
                PathPattern = rule.PathPattern,
                Method = rule.Method,
                BodyJson = bodyJson ?? "",
                QueryString = queryString ?? "",
                Time = DateTime.Now,
                DecidedAt = DateTime.Now,
                Status = "AutoPass",
                Attempts = 1,
                MockRuleId = rule.Id,
                MockRuleDescription = rule.Description,
                RuleJson = JsonSerializer.Serialize(rule)
            };
            Persist(_events[key]);
            TrimEvents(MaxEvents);
            BroadcastEvents();
        }
    }

    public void Decide(string key, bool allow)
    {
        lock (_lock)
        {
            _decisions[key] = allow;
            if (_events.TryGetValue(key, out var ev))
            {
                // 决策立即反映到状态（Approved/Rejected），GRCS 下次重试时消费并返回审批结果
                ev.Status = allow ? "Approved" : "Rejected";
                ev.DecidedAt = DateTime.Now;
                Persist(ev);
            }
            BroadcastEvents();
        }
    }

    public void RemoveEvent(string key) { lock (_lock) { _events.Remove(key); _decisions.Remove(key); RemoveEventDb(key); BroadcastEvents(); } }

    /// <summary>清除已处理（非 Pending）事件；未审批的 Pending 保留。</summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            var pending = _events.Where(kv => kv.Value.Status == "Pending").Select(kv => kv.Key).ToHashSet();
            foreach (var key in _events.Keys.Where(k => !pending.Contains(k)).ToList())
            {
                _events.Remove(key);
                _decisions.Remove(key);
            }
            ClearProcessedDb();
            BroadcastEvents();
        }
    }

    /// <summary>审批事件 Key（RuleId + 剔除时间字段后的请求指纹）；重试请求因 msgTime 不同不再漂移，稳定归并为同一条。</summary>
    public static string ComputeKey(MockRuleDto rule, string bodyJson, string queryString)
        => $"{rule.Id}:{ComputeHash(StripTimeFields(bodyJson), StripTimeFields(queryString))}";

    private static string ComputeHash(string a, string b)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes((a ?? "") + (b ?? ""));
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes))[..8];
    }

    /// <summary>剔除请求中的时间类字段（msgTime/time/timestamp 等，大小写不敏感），body 为 JSON 时按属性剔除，query 按键剔除。</summary>
    private static string StripTimeFields(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            var jo = Newtonsoft.Json.Linq.JObject.Parse(s);
            foreach (var prop in jo.Properties().ToList())
                if (IsTimeField(prop.Name)) prop.Remove();
            return jo.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch { }
        return string.Join('&', (s ?? "").Split('&')
            .Where(p => !IsTimeField(p.Split('=')[0].Trim())));
    }

    private static bool IsTimeField(string name)
        => name.Equals("msgTime", StringComparison.OrdinalIgnoreCase)
        || name.Equals("msg_time", StringComparison.OrdinalIgnoreCase)
        || name.Equals("time", StringComparison.OrdinalIgnoreCase)
        || name.Equals("timestamp", StringComparison.OrdinalIgnoreCase);
}
