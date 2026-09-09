using System.Text.Json;
using WCSBackend.Modules.Wcs.Proxy.Services;
using Contracts.Dtos;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Automation.Services;
using Microsoft.AspNetCore.Mvc;

namespace WCSBackend.Modules.Wcs.Proxy.Controllers;

/// <summary>
/// GRCS 对接代理（/api/wcs/grcs/*，供 WCS 前端调用，后端代发 GRCS 8224）。
/// 前端不再直连 GRCS：GRCS 地址与场景名一律从 WcsSettingsService 读（前端在地图信息页保存，
/// 未保存用默认 localhost:8224 / Show），payload 中的 Warehouse/SceneName 用设置值覆盖，
/// 保证「配置单一数据源」。非 map 接口统一返回 { ok, code, json }，map 返回 zip 字节流。
/// </summary>
[ApiController]
[Route("api/wcs/grcs")]
public class GrcsProxyController : ControllerBase
{
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly ModuleRunService _moduleRun;
    private readonly ILogger<GrcsProxyController> _logger;

    public GrcsProxyController(GrcsHttpClient grcs, WcsSettingsService settings, ModuleRunService moduleRun, ILogger<GrcsProxyController> logger)
    {
        _grcs = grcs;
        _settings = settings;
        _moduleRun = moduleRun;
        _logger = logger;
    }

    /// <summary>记录 WCS 前端实际提交的报文（排查用，含响应摘要）。</summary>
    private void LogProxy(string endpoint, object payload, bool ok, int code, string json)
    {
        _logger.LogInformation(
            "GRCS 代理 {Endpoint} 入站: {Payload} | 结果 ok={Ok} code={Code} 响应={Json}",
            endpoint, JsonSerializer.Serialize(payload), ok, code, json);
    }

    private string BaseUrl => _settings.Get().GrcsBaseUrl;
    private string SceneName => _settings.Get().SceneName;

    /// <summary>任务组下发代理（GRCS /api/v1/task_receive）。</summary>
    [HttpPost("task-receive")]
    public async Task<ActionResult<object>> TaskReceive([FromBody] WcsTaskGroup payload)
    {
        payload.Warehouse = SceneName;
        var (ok, code, json) = await _grcs.SendTaskGroupAsync(BaseUrl, payload);
        LogProxy("task-receive", payload, ok, code, json);
        return Ok(new { ok, code, json });
    }

    /// <summary>车辆任务代理（GRCS /api/RawOrder/ChangeFloor）。</summary>
    [HttpPost("change-floor")]
    public async Task<ActionResult<object>> ChangeFloor([FromBody] VehicleOrderRequest payload)
    {
        payload.SceneName = SceneName;
        var (ok, code, json) = await _grcs.SendVehicleOrderAsync(BaseUrl, payload);
        LogProxy("change-floor", payload, ok, code, json);
        return Ok(new { ok, code, json });
    }

    /// <summary>库存查询代理（GRCS /api/Cargo，场景按设置）。</summary>
    [HttpGet("cargo")]
    public async Task<ActionResult<object>> Cargo([FromQuery] string? code, [FromQuery] string? locked,
        [FromQuery] int pageNo = 1, [FromQuery] int pageSize = 2000)
    {
        var (ok, code2, json) = await _grcs.QueryCargoInventoryAsync(BaseUrl, SceneName, code, locked, pageNo, pageSize);
        return Ok(new { ok, code = code2, json });
    }

    /// <summary>模拟生成容器入库代理（GRCS /AutoContainerEnter，场景按设置）。</summary>
    [HttpGet("auto-container-enter")]
    public async Task<ActionResult<object>> AutoContainerEnter([FromQuery] string prefix = "container",
        [FromQuery] int num = -1, [FromQuery] int floor = -1, [FromQuery] int type = 1)
    {
        var (ok, code, json) = await _grcs.AutoContainerEnterAsync(BaseUrl, SceneName, prefix, num, floor, type);
        return Ok(new { ok, code, json });
    }

    /// <summary>地图 zip 下载代理（GRCS /api/Map/GetMap，场景按设置），成功返回字节流。</summary>
    [HttpGet("map")]
    public async Task<ActionResult> Map()
    {
        var (ok, code, bytes, error) = await _grcs.GetMapBytesAsync(BaseUrl, SceneName);
        if (!ok) return Ok(new { ok = false, code, json = error });
        return File(bytes, "application/octet-stream", "map.zip");
    }

    /// <summary>GRCS 存活探测代理（前端健康轮询用；轻量，2 秒短超时）。</summary>
    [HttpGet("health")]
    public async Task<ActionResult<object>> Health()
    {
        var ok = await _grcs.PingAsync(BaseUrl);
        return Ok(new { ok });
    }

    /// <summary>
    /// 任务组下发（含三类模块后端执行）：POST /api/wcs/task/send。
    /// 入参 WcsTaskGroup（前端只组单任务组），后端经 ModuleRunService 统一跑
    /// 起点模块(下发前) → 下发 GRCS /api/v1/task_receive → 起点之后模块(下发成功后)；
    /// 终点模块由 FinishedModuleWatcher 在任务 FINISHED 后自动执行（框架统一）。
    /// 响应只回显下发结果 { ok, code, json }，模块明细在「模块执行记录」面板看。
    /// </summary>
    [HttpPost("task/send")]
    [Route("/api/wcs/task/send")]
    public async Task<ActionResult<object>> TaskSend([FromBody] WcsTaskGroup payload)
    {
        if (payload == null || payload.Tasks == null || payload.Tasks.Count == 0)
            return Ok(new { ok = false, code = 0, json = "任务组为空" });
        payload.Warehouse = SceneName;
        var (ok, code, json) = await _moduleRun.SendTaskWithModulesAsync(payload);
        LogProxy("task/send", payload, ok, code, json);
        return Ok(new { ok, code, json });
    }
}