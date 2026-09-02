using Newtonsoft.Json.Linq;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Modules.Wcs.Proxy.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 通用 HTTP 转发接口（/api/wcs/forward）。
/// 前端把「GRCS 相对路径 + HTTP 方法 + 报文」交给本接口，后端持有 GRCS 地址/场景名，
/// 在报文注入 Warehouse（场景名，单一数据源）后原样转发到 GRCS，返回 { ok, code, json }。
/// 取代原先分散的 container_ready / container_remove / container_operation_finish 等专用接口；
/// 货物到达 / 货物移除 / 分拣完成等信号统一经此转发（功能模块执行、手动信号均走这里）。
/// </summary>
[ApiController]
[Route("api/wcs")]
public class ForwardController : ControllerBase
{
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly ILogger<ForwardController> _logger;

    public ForwardController(GrcsHttpClient grcs, WcsSettingsService settings, ILogger<ForwardController> logger)
    {
        _grcs = grcs;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>通用转发请求：GRCS 相对路径 + 方法 + 报文。</summary>
    public class ForwardRequest
    {
        /// <summary>GRCS 相对路径，如 /api/v1/container_remove。</summary>
        public string Url { get; set; } = "";

        /// <summary>HTTP 方法，默认 POST（支持 GET/POST/PUT/DELETE）。</summary>
        public string Method { get; set; } = "POST";

        /// <summary>报文（前端已按 WorkValueSource 解析好的参数）；可为空（GET）。</summary>
        public object? Body { get; set; }
    }

    /// <summary>通用转发：注入 Warehouse（场景名）后原样转发到 GRCS。</summary>
    [HttpPost("forward")]
    public async Task<ActionResult<object>> Forward([FromBody] ForwardRequest? req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { ok = false, code = 400, json = "url 不能为空" });

        var settings = _settings.Get();
        var target = settings.GrcsBaseUrl.TrimEnd('/') + req.Url;
        var method = ParseMethod(req.Method);

        // 注入 Warehouse（场景名），保证后端为唯一数据源
        string? rawJson = null;
        if (req.Body != null)
        {
            if (req.Body is JObject jo)
            {
                jo["Warehouse"] = settings.SceneName;
                rawJson = jo.ToString();
            }
            else
            {
                rawJson = Newtonsoft.Json.JsonConvert.SerializeObject(req.Body);
            }
        }

        var (ok, code, json) = await _grcs.ForwardAsync(target, method, rawJson);
        _logger.LogInformation("通用转发 {Method} {Url} -> {Target} 结果 code={Code} ok={Ok}", req.Method, req.Url, target, code, ok);
        return Ok(new { ok, code, json });
    }

    private static System.Net.Http.HttpMethod ParseMethod(string method) => method?.ToUpperInvariant() switch
    {
        "GET" => System.Net.Http.HttpMethod.Get,
        "PUT" => System.Net.Http.HttpMethod.Put,
        "DELETE" => System.Net.Http.HttpMethod.Delete,
        _ => System.Net.Http.HttpMethod.Post,
    };
}
