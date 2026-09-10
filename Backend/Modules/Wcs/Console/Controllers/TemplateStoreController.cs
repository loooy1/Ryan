using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 任务类型模板 + 功能模板存储接口（/api/wcs/templates、/api/wcs/modules）。
/// 前端在任务下发页/信号交互页创建模板后 POST 持久化到 SQLite，跨浏览器共享；
/// 其他页面/设备 GET 拉取同一份（替换 localStorage 单机存储）。
/// </summary>
[ApiController]
[Route("api/wcs/templates")]
public class TaskTemplateController : ControllerBase
{
    private readonly TaskTemplateStore _store;

    public TaskTemplateController(TaskTemplateStore store) => _store = store;

    /// <summary>全部任务类型模板。</summary>
    [HttpGet]
    public ActionResult<object> GetAll() => Ok(new { success = true, items = _store.GetAll() });

    /// <summary>整体替换任务类型模板列表（前端保存整个自定义集）。</summary>
    [HttpPost]
    public ActionResult<object> ReplaceAll([FromBody] List<TaskTemplateDto> items)
    {
        _store.ReplaceAll(items ?? []);
        return Ok(new { success = true, count = _store.GetAll().Count });
    }

    /// <summary>按 Value 删除一条任务类型模板。</summary>
    [HttpDelete("{value}")]
    public ActionResult<object> Remove(string value)
    {
        var ok = _store.Remove(value);
        return Ok(new { success = ok, value });
    }
}

/// <summary>
/// 功能模板存储接口（/api/wcs/modules）。
/// </summary>
[ApiController]
[Route("api/wcs/modules")]
public class FeatureModuleController : ControllerBase
{
    private readonly FeatureModuleStore _store;

    public FeatureModuleController(FeatureModuleStore store)
    {
        _store = store;
    }

    /// <summary>全部功能模板。</summary>
    [HttpGet]
    public ActionResult<object> GetAll() => Ok(new { success = true, items = _store.GetAll() });

    /// <summary>整体替换功能模板列表（前端保存整个自定义集）。</summary>
    [HttpPost]
    public ActionResult<object> ReplaceAll([FromBody] List<FeatureModuleDto> items)
    {
        _store.ReplaceAll(items ?? []);
        return Ok(new { success = true, count = _store.GetAll().Count });
    }

    /// <summary>按 Id 删除一条功能模板。</summary>
    [HttpDelete("{id}")]
    public ActionResult<object> Remove(string id)
    {
        var ok = _store.Remove(id);
        return Ok(new { success = ok, id });
    }

}
