using Contracts.Dtos;
using Microsoft.AspNetCore.Http;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class MockRuleStore
{
    private readonly RcsProtocolTemplateStore _templates;

    public MockRuleStore(RcsProtocolTemplateStore templates) => _templates = templates;

    /// <summary>是否存在启用的任务阶段卡（BoardSync=true，URL 任意）——无此卡时禁止下发任务。</summary>
    public bool HasTaskStageRule()
    {
        return GetAll().Any(r => r.Enabled && r.BoardSync);
    }

    public List<MockRuleDto> GetAll()
    {
        return _templates.GetActive().MockRules;
    }

    public void ReplaceAll(IEnumerable<MockRuleDto> items)
    {
        _templates.ReplaceActiveMockRules(items ?? []);
    }

    public bool Remove(string id)
    {
        return _templates.RemoveActiveMockRule(id);
    }

    /// <summary>按优先级匹配首条命中规则（Enabled 且 Method+Path 匹配且所有 Matcher 满足）。</summary>
    public MockRuleDto? Match(string method, string path, IQueryCollection query, string? bodyJson)
    {
        var snapshot = GetAll().Where(r => r.Enabled).OrderByDescending(r => r.Priority).ThenBy(r => r.Id).ToList();
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
        var snapshot = GetAll().Where(r => r.Enabled).OrderByDescending(r => r.Priority).ThenBy(r => r.Id).ToList();
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
