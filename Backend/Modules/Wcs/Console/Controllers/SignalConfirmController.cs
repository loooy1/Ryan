using GrcsBackend.Modules.Wcs.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace GrcsBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 信号确认状态接口（/api/wcs/signal-confirm）：
/// GET 全量（前端 1s 轮询跨标签页同步）。
/// kind 枚举：arrival / removal / sent / module_after / module_end。
/// （module_after = 任务模板「起点之后」模块执行抢占；module_end = 任务模板「终点」模块执行抢占。）
/// 抢占/撤销已下沉后端（SignalAutoHostedService 内部完成），不再对外提供 POST/DELETE。
/// </summary>
[ApiController]
[Route("api/wcs/signal-confirm")]
public class SignalConfirmController : ControllerBase
{
    private readonly SignalConfirmStore _store;

    public SignalConfirmController(SignalConfirmStore store) => _store = store;

    [HttpGet]
    public ActionResult<object> GetAll() => Ok(_store.GetAll());
}