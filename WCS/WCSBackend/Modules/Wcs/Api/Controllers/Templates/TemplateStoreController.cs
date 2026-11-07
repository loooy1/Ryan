using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Realtime;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 任务类型模板 + 功能模板存储接口（/api/wcs/templates、/api/wcs/modules）。
/// 前端在任务下发页/信号交互页创建模板后 POST 持久化到 MySQL，跨浏览器共享；
/// 其他页面/设备 GET 拉取同一份（替换 localStorage 单机存储）。
/// </summary>
[ApiController]
[Route("api/wcs/templates")]
public class TaskTemplateController : ControllerBase
{
    private readonly TaskTemplateStore _store;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public TaskTemplateController(TaskTemplateStore store, IHubContext<TaskStageRealtimeHub> hub) { _store = store; _hub = hub; }

    /// <summary>全部任务类型模板。</summary>
    [HttpGet]
    public ActionResult<object> GetAll([FromQuery] string? profileId) => Ok(new { success = true, items = _store.GetAll(profileId) });

    /// <summary>整体替换任务类型模板列表（前端保存整个自定义集）。</summary>
    [HttpPost]
    public ActionResult<object> ReplaceAll([FromBody] List<TaskTemplateDto> items, [FromQuery] string? profileId)
    {
        _store.ReplaceAll(items ?? [], profileId);
        _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "task-templates");
        return Ok(new { success = true, count = _store.GetAll(profileId).Count });
    }

    /// <summary>按 Value 删除一条任务类型模板。</summary>
    [HttpDelete("{value}")]
    public ActionResult<object> Remove(string value, [FromQuery] string? profileId)
    {
        var ok = _store.Remove(value, profileId);
        if (ok) _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "task-templates");
        return Ok(new { success = ok, value });
    }
}

/// <summary>
/// 当前 RCS 配置集的功能模板接口（/api/wcs/modules）。
/// </summary>
[ApiController]
[Route("api/wcs/modules")]
public class FeatureModuleController : ControllerBase
{
    private readonly RcsProtocolTemplateStore _store;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public FeatureModuleController(RcsProtocolTemplateStore store, IHubContext<TaskStageRealtimeHub> hub)
    {
        _store = store;
        _hub = hub;
    }

    /// <summary>全部功能模板。</summary>
    [HttpGet]
    public ActionResult<object> GetAll([FromQuery] string? profileId) => Ok(new { success = true, items = _store.GetFeatureModules(profileId) });

    /// <summary>整体替换功能模板列表（前端保存整个自定义集）。</summary>
    [HttpPost]
    public ActionResult<object> ReplaceAll([FromBody] List<FeatureModuleDto> items, [FromQuery] string? profileId)
    {
        _store.ReplaceFeatureModules(items ?? [], profileId);
        _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "feature-modules");
        return Ok(new { success = true, count = _store.GetFeatureModules(profileId).Count });
    }

    /// <summary>按 Id 删除一条功能模板。</summary>
    [HttpDelete("{id}")]
    public ActionResult<object> Remove(string id, [FromQuery] string? profileId)
    {
        var existing = _store.GetFeatureModules(profileId);
        var remaining = existing.Where(x => !string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        var ok = remaining.Count != existing.Count;
        if (ok) _store.ReplaceFeatureModules(remaining, profileId);
        if (ok) _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "feature-modules");
        return Ok(new { success = ok, id });
    }

}
