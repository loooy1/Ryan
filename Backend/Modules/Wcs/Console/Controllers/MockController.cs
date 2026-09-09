using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 通用 Mock 规则管理接口（/api/wcs/mocks）。
/// 前端在信号交互页可配任意入站 URL + 参数匹配 → 自定义返回值，无需硬编码。
/// </summary>
[ApiController]
[Route("api/wcs/mocks")]
public class MockRuleController : ControllerBase
{
    private readonly MockRuleStore _store;
    public MockRuleController(MockRuleStore store) => _store = store;

    [HttpGet]
    public ActionResult<object> GetAll() => Ok(new { success = true, items = _store.GetAll() });

    [HttpPost]
    public ActionResult<object> ReplaceAll([FromBody] List<MockRuleDto> items)
    {
        _store.ReplaceAll(items ?? []);
        return Ok(new { success = true, count = _store.GetAll().Count });
    }
}

/// <summary>
/// 通用 Mock 命中接口（/api/mock/{*path}）及全动态业务入口（/api/v1/* 已注释原固定控制器，由此统一接管）。
/// </summary>
[ApiController]
public class MockHitController : ControllerBase
{
    private readonly MockRuleStore _store;
    private readonly ILogger<MockHitController> _logger;
    private readonly WCSBackend.Modules.Wcs.Console.Services.ITaskStageService? _stages;
    private readonly WCSBackend.Modules.Wcs.Console.Services.MockApprovalService? _mockApproval;
    public MockHitController(MockRuleStore store, ILogger<MockHitController> logger, IServiceProvider sp)
    {
        _store = store;
        _logger = logger;
        _stages = sp.GetService(typeof(WCSBackend.Modules.Wcs.Console.Services.ITaskStageService)) as WCSBackend.Modules.Wcs.Console.Services.ITaskStageService;
        _mockApproval = sp.GetService(typeof(WCSBackend.Modules.Wcs.Console.Services.MockApprovalService)) as WCSBackend.Modules.Wcs.Console.Services.MockApprovalService;
    }

    [Route("api/mock/{*path}", Order = 999)]
    [Route("api/v1/mock/{*path}", Order = 999)]
    [Route("api/{*path}", Order = 999)]
    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH")]
    public async Task<IActionResult> Hit(string? path)
    {
        var method = HttpContext.Request.Method;
        var fullPath = "/" + (path ?? "");
        // 保留原始完整路径用于匹配（含 /api/mock 前缀的也按完整路径匹配，规则可填完整或前缀）
        var rawPath = HttpContext.Request.Path.Value ?? fullPath;
        // 兼容前端填的 PathPattern 可能是 /api/v1/station_entry_request 或 /api/mock/... 都能命中
        string? bodyJson = null;
        if (HttpContext.Request.ContentLength > 0)
        {
            HttpContext.Request.EnableBuffering();
            using var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
            bodyJson = await reader.ReadToEndAsync();
            HttpContext.Request.Body.Position = 0;
            if (string.IsNullOrWhiteSpace(bodyJson)) bodyJson = null;
        }

        var rule = _store.Match(method, rawPath, HttpContext.Request.Query, bodyJson)
                ?? _store.Match(method, fullPath, HttpContext.Request.Query, bodyJson)
                ?? _store.Match(method, rawPath.Replace("/api/mock", "/api", StringComparison.OrdinalIgnoreCase), HttpContext.Request.Query, bodyJson);

        if (rule != null)
        {
            // 需审批的 Mock：按规则生成请求任务，审批控 ResponseBody 中 ApprovalVariable 变量
            if (rule.RequiresApproval && _mockApproval != null)
            {
                var queryStr = HttpContext.Request.QueryString.Value ?? "";
                var (hasDecision, allow) = _mockApproval.TryConsumeDecision(rule, bodyJson ?? "", queryStr);
                if (!hasDecision)
                {
                    // 首次命中无决策则回等待态（RCS 将循环重试），前端在请求信号中批准/拒绝后下次重试即按审批结果返回
                    _logger.LogInformation("Mock 审批等待 {Method} {Path} 规则 {Id}", method, rawPath, rule.Id);
                    var wk = WCSBackend.Modules.Wcs.Console.Services.MockApprovalService.ComputeKey(rule, bodyJson ?? "", queryStr);
                    return Ok(new { success = false, message = "等待审批", approvalPending = true, key = wk });
                }
                // 已审批则按审批结果渲染 ApprovalVariable
                var approvalVal = allow ? rule.ApprovalTrueValue : rule.ApprovalFalseValue;
                var bodyWithApproval = rule.ResponseBody.Replace("{{approval}}", approvalVal, StringComparison.OrdinalIgnoreCase)
                                                        .Replace($"{{{{approval.{rule.ApprovalVariable}}}}}", approvalVal, StringComparison.OrdinalIgnoreCase);
                // 若 ResponseBody 为 JSON 且含 ApprovalVariable 字段则直接替换该字段值
                try
                {
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrWhiteSpace(bodyWithApproval) ? "{}" : bodyWithApproval);
                    // 尝试按 ApprovalVariable 设值（支持 success / allow 等布尔或字符串）
                    if (jo[rule.ApprovalVariable] != null)
                    {
                        if (bool.TryParse(approvalVal, out var b)) jo[rule.ApprovalVariable] = b;
                        else if (int.TryParse(approvalVal, out var iv)) jo[rule.ApprovalVariable] = iv;
                        else jo[rule.ApprovalVariable] = approvalVal;
                        bodyWithApproval = jo.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    // 统一渲染占位符（{{now}} / {{body.*}} / {{query.*}}），否则 msgTime:"{{now}}" 原样返回给 GRCS 会解析失败
                    bodyWithApproval = RenderPlaceholders(bodyWithApproval, HttpContext.Request.Query, bodyJson);
                }
                catch { bodyWithApproval = RenderPlaceholders(bodyWithApproval, HttpContext.Request.Query, bodyJson); }

if (rule.BoardSync)
                {
                    try
                    {
                        if (_stages != null)
                        {
                            var m = BuildBoardModel(bodyJson, HttpContext.Request.Query);
                            if (m != null) _stages.Record(m);
                        }
                    }
                    catch (Exception ex) { _logger.LogError(ex, "BoardSync 落库异常: {Raw}", rawPath); }
                }
                _logger.LogInformation("Mock 审批命中 {Method} {Path} 规则 {Id} 审批 {Allow} 返回 {Code}", method, rawPath, rule.Id, allow, rule.ResponseCode);
                try
                {
                    var jo2 = Newtonsoft.Json.Linq.JToken.Parse(string.IsNullOrWhiteSpace(bodyWithApproval) ? "{}" : bodyWithApproval);
                    return StatusCode(rule.ResponseCode == 0 ? 200 : rule.ResponseCode, jo2);
                }
                catch { return StatusCode(rule.ResponseCode == 0 ? 200 : rule.ResponseCode, bodyWithApproval); }
            }

            if (rule.BoardSync)
            {
                try
                {
                    if (_stages != null)
                    {
                        var m = BuildBoardModel(bodyJson, HttpContext.Request.Query);
                        if (m != null) _stages.Record(m);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "BoardSync 落库异常: {Raw}", rawPath); }
            }
            // 自动通过（无需审批）的命中同样记录到请求信号页（持久化，重启不丢）
            if (!rule.RequiresApproval && _mockApproval != null)
                _mockApproval.RecordAutoPass(rule, bodyJson ?? "", HttpContext.Request.QueryString.Value ?? "");
            var body2 = RenderPlaceholders(rule.ResponseBody, HttpContext.Request.Query, bodyJson);
            _logger.LogInformation("Mock 命中 {Method} {Path} 规则 {Id} 返回 {Code}", method, rawPath, rule.Id, rule.ResponseCode);
            try
            {
                var jo = Newtonsoft.Json.Linq.JToken.Parse(string.IsNullOrWhiteSpace(body2) ? "{}" : body2);
                return StatusCode(rule.ResponseCode == 0 ? 200 : rule.ResponseCode, jo);
            }
            catch { return StatusCode(rule.ResponseCode == 0 ? 200 : rule.ResponseCode, body2); }
        }

        // 未命中：不配置的请求不接收（统一 404，RCS 收到失败会重试；看板只显示命中卡片的数据）
        var why = _store.Diagnose(method, rawPath, HttpContext.Request.Query, bodyJson);
        _logger.LogWarning("Mock 未命中 {Method} {Path} Query={Query} Body={Body} 原因={Why}", method, rawPath, HttpContext.Request.QueryString.Value ?? "", bodyJson ?? "", why ?? "无匹配卡片");
        return NotFound(new { success = false, message = $"Mock 未命中: {method} {rawPath}，请在信号交互→通用 Mock 入站新建该 URL 的卡" });
    }

        /// <summary>
    /// 构造任务看板阶段记录（URL 无关）：从 body/query 大小写不敏感自动识别 taskId/stage/msgTime 等字段
    /// （兼容 taskId/task_code/orderNo、stage/status/state、msgTime/time 等别名）。
    /// 识别不到 taskId+stage 返回 null（不落库，避免脏数据）。
    /// </summary>
    private Contracts.Dtos.TaskStageChangeModel? BuildBoardModel(string? bodyJson, IQueryCollection query)
    {
        Newtonsoft.Json.Linq.JObject? body = null;
        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            try { body = Newtonsoft.Json.Linq.JObject.Parse(bodyJson); } catch { }
        }

        string? taskId = Pick(body, query, "taskId", "task_id", "taskid", "taskCode", "task_code", "orderNo", "order_no");
        string? stage = Pick(body, query, "stage", "status", "state", "current_stage");
        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(stage)) return null;

        var model = new Contracts.Dtos.TaskStageChangeModel { TaskId = taskId, Stage = stage };
        var timeStr = Pick(body, query, "msgTime", "msg_time", "time", "timestamp");
        model.MsgTime = DateTime.TryParse(timeStr, out var t) ? t : DateTime.Now;
        model.Warehouse = Pick(body, query, "warehouse") ?? "";
        model.StationCode = Pick(body, query, "stationCode", "station_code", "station") ?? "";
        model.ContainerCode = Pick(body, query, "containerCode", "container_code", "container") ?? "";
        return model;
    }

    private static string? Pick(Newtonsoft.Json.Linq.JObject? body, IQueryCollection query, params string[] keys)
    {
        if (body != null)
        {
            var prop = body.Properties().FirstOrDefault(p => keys.Any(k => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase)));
            if (prop != null)
            {
                var v = prop.Value?.ToString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        foreach (var k in keys)
            if (query.TryGetValue(k, out var qv)) return qv.ToString();
        return null;
    }

    private static string RenderPlaceholders(string template, IQueryCollection query, string? bodyJson)
    {
        if (string.IsNullOrEmpty(template)) return "{}";
        var result = template;
        // {{query.key}} / {{body.key}}
        foreach (var kv in query)
            result = result.Replace($"{{{{query.{kv.Key}}}}}", kv.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                           .Replace($"{{{{query.{kv.Key.ToLower()}}}}}", kv.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(bodyJson);
                foreach (var prop in jo.Properties())
                    result = result.Replace($"{{{{body.{prop.Name}}}}}", prop.Value.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }
        result = result.Replace("{{now}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
