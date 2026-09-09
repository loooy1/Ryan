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
    private readonly ModuleExecLogStore _execLog;
    private readonly ModuleRunService _moduleRun;

    public FeatureModuleController(FeatureModuleStore store, ModuleExecLogStore execLog, ModuleRunService moduleRun)
    {
        _store = store;
        _execLog = execLog;
        _moduleRun = moduleRun;
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

    /// <summary>清空已处理：只删除成功（HTTP 2xx）的模块执行记录，失败/异常记录保留（广播剩余快照实时同步前端）。</summary>
    [HttpDelete("logs")]
    public ActionResult<object> ClearModuleExecLogs() { _execLog.ClearProcessed(); return Ok(new { success = true }); }

    /// <summary>重试单条模块执行记录：按记录恢复任务上下文后重新 POST 该模块（MsgTime 用当前时间），新记录经 SignalR 实时推送。</summary>
    [HttpPost("logs/{id:long}/retry")]
    public async Task<ActionResult<object>> RetryModuleLog(long id)
    {
        var entry = _execLog.GetById(id);
        if (entry == null) return NotFound(new { success = false, message = "记录不存在" });
        await _moduleRun.RetryEntryAsync(entry);
        return Ok(new { success = true, id });
    }
}