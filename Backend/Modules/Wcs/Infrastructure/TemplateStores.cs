using Backend.Shared.Infrastructure.Repository;
using Contracts.Entities;
using Contracts.Dtos;
using Mapster;
using Microsoft.AspNetCore.Http;

namespace WCSBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// 任务类型模板存储（内存 + EF Core 持久化 task_templates 表）。
/// 跨浏览器/换机共享：前端创建模板后 POST 到这里，其他页面/标签页/浏览器拉取同一份。
/// Singleton。
/// </summary>
public class TaskTemplateStore
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<TaskTemplateDto> _items = [];

    public TaskTemplateStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            using var uow = _uow.Create();
            _items = uow.Repository<TaskTemplateRow>().FindAllAsync().GetAwaiter().GetResult()
                .Select(r => r.Adapt<TaskTemplateDto>()).ToList();
        }
        catch { }
    }

    public List<TaskTemplateDto> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public void ReplaceAll(IEnumerable<TaskTemplateDto> items)
    {
        lock (_lock)
        {
            _items = items?.ToList() ?? [];
            using var uow = _uow.Create();
            var repo = uow.Repository<TaskTemplateRow>();
            repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
            foreach (var dto in _items)
                repo.AddAsync(dto.Adapt<TaskTemplateRow>()).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    public bool Remove(string value)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                using var uow = _uow.Create();
                uow.Repository<TaskTemplateRow>().DeleteWhereAsync(x => x.Value == value).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            return n > 0;
        }
    }
}

/// <summary>
/// 功能模板存储（内存 + EF Core 持久化 feature_modules 表）。
/// 跨浏览器/换机共享：前端创建模块后 POST 到这里，其他页面/标签页/浏览器拉取同一份。
/// Singleton。
/// </summary>
public class FeatureModuleStore
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<FeatureModuleDto> _items = [];

    public FeatureModuleStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            using var uow = _uow.Create();
            _items = uow.Repository<FeatureModuleRow>().FindAllAsync().GetAwaiter().GetResult()
                .Select(r => r.Adapt<FeatureModuleDto>()).ToList();
        }
        catch { }
    }

    public List<FeatureModuleDto> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public void ReplaceAll(IEnumerable<FeatureModuleDto> items)
    {
        lock (_lock)
        {
            _items = items?.ToList() ?? [];
            using var uow = _uow.Create();
            var repo = uow.Repository<FeatureModuleRow>();
            repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
            foreach (var dto in _items)
                repo.AddAsync(dto.Adapt<FeatureModuleRow>()).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                using var uow = _uow.Create();
                uow.Repository<FeatureModuleRow>().DeleteWhereAsync(x => x.Id == id).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            return n > 0;
        }
    }
}

/// <summary>
/// 自动化模板存储（内存 + EF Core 持久化 auto_templates 表）。
/// 跨浏览器/换机共享：前端创建模板后 POST 到这里，其他页面/标签页/浏览器拉取同一份。
/// Singleton。
/// </summary>
public class AutoTemplateStore
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<AutoTemplateDto> _items = [];

    public AutoTemplateStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            using var uow = _uow.Create();
            _items = uow.Repository<AutoTemplateRow>().FindAllAsync().GetAwaiter().GetResult()
                .Select(r => r.Adapt<AutoTemplateDto>()).ToList();
        }
        catch { }
    }

    public List<AutoTemplateDto> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public void ReplaceAll(IEnumerable<AutoTemplateDto> items)
    {
        lock (_lock)
        {
            _items = items?.ToList() ?? [];
            using var uow = _uow.Create();
            var repo = uow.Repository<AutoTemplateRow>();
            repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
            foreach (var dto in _items)
                repo.AddAsync(dto.Adapt<AutoTemplateRow>()).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>单条保存：替换内存中同 Id 项（不存在则追加）并持久化，不影响其他模板。</summary>
    public void Upsert(AutoTemplateDto item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) return;
        lock (_lock)
        {
            var idx = _items.FindIndex(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _items[idx] = item; else _items.Add(item);
            using var uow = _uow.Create();
            var repo = uow.Repository<AutoTemplateRow>();
            var row = repo.FindAsync(item.Id).GetAwaiter().GetResult();
            if (row == null)
                repo.AddAsync(item.Adapt<AutoTemplateRow>()).GetAwaiter().GetResult();
            else
                item.Adapt(row);
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                using var uow = _uow.Create();
                uow.Repository<AutoTemplateRow>().DeleteWhereAsync(x => x.Id == id).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            return n > 0;
        }
    }
}

/// <summary>
/// 通用 Mock 规则存储（内存 + EF Core 持久化 mock_rules 表）。
/// </summary>
public class MockRuleStore
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<MockRuleDto> _items = [];

    public MockRuleStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            using var uow = _uow.Create();
            _items = uow.Repository<MockRuleRow>().FindAllAsync().GetAwaiter().GetResult()
                .Select(r => r.Adapt<MockRuleDto>()).ToList();
        }
        catch { }
        EnsureDefaultRule();
    }

    /// <summary>确保任务阶段模板卡存在：已有任何启用的任务阶段卡（BoardSync）则不干预；MOCK_DEFAULT 缺失时插入，路径非模板时迁移。</summary>
    private void EnsureDefaultRule()
    {
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(x => string.Equals(x.Id, "MOCK_DEFAULT", StringComparison.OrdinalIgnoreCase));
            if (existing != null && string.Equals(existing.PathPattern, "/api/v1/task_stage_change", StringComparison.OrdinalIgnoreCase))
                return;
            // 已有用户自建的任务阶段卡（任意 URL）→ 不再强制模板卡
            if (existing == null && _items.Any(x => x.Enabled && x.BoardSync))
                return;
            var rule = new MockRuleDto
            {
                Id = "MOCK_DEFAULT",
                Method = "POST",
                PathPattern = "/api/v1/task_stage_change",
                Matchers = [],
                ResponseCode = 200,
                ResponseBody = "{\"success\":true,\"message\":\"ok\"}",
                Enabled = true,
                Priority = 0,
                Description = "任务阶段模板卡：勾选「关联任务看板」即为任务阶段卡（URL 可任意改），命中时自动提取 taskId/stage 推送任务看板。系统要求至少存在一张任务阶段卡才允许下发任务。",
                AlsoRecord = true,
                BoardSync = true,
                RequiresApproval = false,
            };
            if (existing == null) _items.Add(rule);
            else
            {
                var idx = _items.FindIndex(x => string.Equals(x.Id, "MOCK_DEFAULT", StringComparison.OrdinalIgnoreCase));
                _items[idx] = rule;
            }
            PersistAll();
        }
    }

    /// <summary>是否存在启用的任务阶段卡（BoardSync=true，URL 任意）——无此卡时禁止下发任务。</summary>
    public bool HasTaskStageRule()
    {
        lock (_lock) return _items.Any(r => r.Enabled && r.BoardSync);
    }

    public List<MockRuleDto> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public void ReplaceAll(IEnumerable<MockRuleDto> items)
    {
        lock (_lock)
        {
            _items = items?.ToList() ?? [];
            EnforceSingleBoardSync();
            PersistAll();
        }
    }

    /// <summary>任务阶段卡（BoardSync）最多一张：保留第一张，其余强制关闭（防多卡争抢看板数据）；且与「需审批」互斥。</summary>
    private void EnforceSingleBoardSync()
    {
        var first = _items.FirstOrDefault(x => x.BoardSync);
        if (first == null) return;
        foreach (var r in _items)
        {
            if (!ReferenceEquals(r, first)) r.BoardSync = false;
            if (r.BoardSync) r.RequiresApproval = false;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                using var uow = _uow.Create();
                uow.Repository<MockRuleRow>().DeleteWhereAsync(x => x.Id == id).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            return n > 0;
        }
    }

    /// <summary>按优先级匹配首条命中规则（Enabled 且 Method+Path 匹配且所有 Matcher 满足）。</summary>
    public MockRuleDto? Match(string method, string path, IQueryCollection query, string? bodyJson)
    {
        List<MockRuleDto> snapshot;
        lock (_lock) snapshot = _items.Where(r => r.Enabled).OrderByDescending(r => r.Priority).ThenBy(r => r.Id).ToList();
        foreach (var rule in snapshot)
        {
            if (!string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase) && rule.Method != "*") continue;
            if (!IsPathMatch(rule.PathPattern, path)) continue;
            if (!IsMatchersMatch(rule.Matchers, query, bodyJson, path)) continue;
            return rule;
        }
        return null;
    }

    /// <summary>未命中诊断：对 Method+Path 匹配的候选卡，返回第一个不满足的匹配条件及实际值（便于排查 404 原因）。</summary>
    public string? Diagnose(string method, string path, IQueryCollection query, string? bodyJson)
    {
        List<MockRuleDto> snapshot;
        lock (_lock) snapshot = _items.Where(r => r.Enabled).OrderByDescending(r => r.Priority).ThenBy(r => r.Id).ToList();
        Newtonsoft.Json.Linq.JObject? body = null;
        if (!string.IsNullOrEmpty(bodyJson))
        {
            try { body = Newtonsoft.Json.Linq.JObject.Parse(bodyJson); } catch { }
        }
        foreach (var rule in snapshot)
        {
            if (!string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase) && rule.Method != "*") continue;
            if (!IsPathMatch(rule.PathPattern, path)) continue;
            foreach (var m in rule.Matchers)
            {
                string? actual = null;
                var key = m.Key ?? "";
                var source = (m.Source ?? "query").ToLowerInvariant();
                if (source == "query") actual = query[key].ToString();
                else if (source == "body" && body != null) actual = ResolveBody(body, key)?.ToString();
                else if (source == "path") actual = path;
                else actual = query[key].ToString() ?? ResolveBody(body, key)?.ToString();
                var op = (m.Op ?? "equals").ToLowerInvariant();
                var expected = m.Expected ?? "";
                bool ok = op switch
                {
                    "equals" => string.Equals(actual ?? "", expected, StringComparison.OrdinalIgnoreCase),
                    "contains" => (actual ?? "").Contains(expected, StringComparison.OrdinalIgnoreCase),
                    "regex" => System.Text.RegularExpressions.Regex.IsMatch(actual ?? "", expected),
                    "exists" => !string.IsNullOrEmpty(actual),
                    "notexists" => string.IsNullOrEmpty(actual),
                    _ => string.Equals(actual ?? "", expected, StringComparison.OrdinalIgnoreCase)
                };
                if (!ok) return $"规则 {rule.Id} 条件不满足: {source}.{key} {op} 期望'{expected}' 实际'{actual ?? "(空)"}'";
            }
        }
        return null;
    }

    private void PersistAll()
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<MockRuleRow>();
        repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
        foreach (var dto in _items)
            repo.AddAsync(dto.Adapt<MockRuleRow>()).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    private static bool IsPathMatch(string pattern, string path)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        pattern = pattern.Trim();
        if (pattern == path) return true;
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern[..^1];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMatchersMatch(List<MockMatcher> matchers, IQueryCollection query, string? bodyJson, string path)
    {
        if (matchers == null || matchers.Count == 0) return true;
        Newtonsoft.Json.Linq.JObject? body = null;
        if (!string.IsNullOrEmpty(bodyJson))
        {
            try { body = Newtonsoft.Json.Linq.JObject.Parse(bodyJson); } catch { }
        }
        foreach (var m in matchers)
        {
            string? actual = null;
            var key = m.Key ?? "";
            var source = (m.Source ?? "query").ToLowerInvariant();
            if (source == "query") actual = query[key].ToString();
            else if (source == "body" && body != null) actual = ResolveBody(body, key)?.ToString();
            else if (source == "path") actual = path;
            else actual = query[key].ToString() ?? ResolveBody(body, key)?.ToString();

            var op = (m.Op ?? "equals").ToLowerInvariant();
            var expected = m.Expected ?? "";
            bool ok = op switch
            {
                "equals" => string.Equals(actual ?? "", expected, StringComparison.OrdinalIgnoreCase),
                "contains" => (actual ?? "").Contains(expected, StringComparison.OrdinalIgnoreCase),
                "regex" => System.Text.RegularExpressions.Regex.IsMatch(actual ?? "", expected),
                "exists" => !string.IsNullOrEmpty(actual),
                "notexists" => string.IsNullOrEmpty(actual),
                _ => string.Equals(actual ?? "", expected, StringComparison.OrdinalIgnoreCase)
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>按 key 取 body 值：先精确 SelectToken（支持嵌套/数组），失败则按 . 分层大小写不敏感回退。</summary>
    private static Newtonsoft.Json.Linq.JToken? ResolveBody(Newtonsoft.Json.Linq.JObject body, string key)
    {
        var t = body.SelectToken(key);
        if (t != null) return t;
        Newtonsoft.Json.Linq.JToken? cur = body;
        foreach (var part in key.Split('.'))
        {
            if (cur is Newtonsoft.Json.Linq.JObject jo)
            {
                var prop = jo.Properties().FirstOrDefault(p => string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase));
                if (prop == null) return null;
                cur = prop.Value;
            }
            else if (cur is Newtonsoft.Json.Linq.JArray arr && int.TryParse(part, out var i) && i >= 0 && i < arr.Count) cur = arr[i];
            else return null;
        }
        return cur;
    }
}