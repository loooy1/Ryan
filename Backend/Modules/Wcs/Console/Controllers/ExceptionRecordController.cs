using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using Contracts.Entities;
using Microsoft.AspNetCore.Mvc;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 异常记录接口（/api/wcs/exception-records）：AGV/软件异常台账。
/// 纯 HTTP（前端推给后端 / 从后端读取），无轮询无 SignalR。
/// GET 列表（发生时间倒序）；POST 新增；PUT 更新；DELETE 删除；
/// POST /{id}/reproduce 复现三联动（次数 +1、复现时间=当前、自动置为未解决）。
/// </summary>
[ApiController]
[Route("api/wcs/exception-records")]
public class ExceptionRecordController : ControllerBase
{
    private readonly ExceptionRecordStore _store;

    public ExceptionRecordController(ExceptionRecordStore store) => _store = store;

    /// <summary>查询参数：vehicle=车号关键字（LIKE 模糊，匹配历史车号任意一个）；dateFrom/dateTo=发生日期(yyyy-MM-dd，可含时间)；status=状态（resolved/pending/in_progress/observing）；dept=责任部门（精确匹配 RCS/WCS/Quicktron）；project=项目名（空串=仅未分类）。</summary>
    [HttpGet]
    public ActionResult<object> GetAll(string? vehicle = null, string? dateFrom = null, string? dateTo = null, string? status = null, string? dept = null, string? project = null)
        => Ok(_store.GetAll(vehicle, dateFrom, dateTo, status, dept, project));

    /// <summary>项目名去重列表（含空串=未分类），供前端下拉切换。</summary>
    [HttpGet("projects")]
    public ActionResult<object> Projects() => Ok(_store.Projects());

    /// <summary>删除某项目及其全部异常记录（需密码，不可恢复）。</summary>
    [HttpDelete("projects/{name}")]
    public ActionResult<object> DeleteProject(string name, [FromQuery] string? password = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { success = false, message = "项目名不能为空" });
        if (password != "wayzim") return StatusCode(403, new { success = false, message = "删除密码错误" });
        _store.RemoveByProject(name.Trim());
        return Ok(new { success = true });
    }

    [HttpPost]
    public ActionResult<object> Add([FromBody] ExceptionRecordDto rec)
    {
        if (rec == null) return BadRequest(new { success = false, message = "请求体不能为空" });
        if (string.IsNullOrWhiteSpace(rec.HappenedAt)) return BadRequest(new { success = false, message = "发生时间不能为空" });
        if (string.IsNullOrWhiteSpace(rec.Phenomenon)) return BadRequest(new { success = false, message = "现象不能为空" });
        if (string.IsNullOrWhiteSpace(rec.ResponsibleDept)) return BadRequest(new { success = false, message = "责任部门不能为空（RCS/WCS/Quicktron）" });
        var id = _store.Add(rec);
        return Ok(new { success = true, id });
    }

    [HttpPut("{id:long}")]
    public ActionResult<object> Update(long id, [FromBody] ExceptionRecordDto rec)
    {
        if (rec == null) return BadRequest(new { success = false, message = "请求体不能为空" });
        if (string.IsNullOrWhiteSpace(rec.HappenedAt)) return BadRequest(new { success = false, message = "发生时间不能为空" });
        if (string.IsNullOrWhiteSpace(rec.ResponsibleDept)) return BadRequest(new { success = false, message = "责任部门不能为空（RCS/WCS/Quicktron）" });
        rec.Id = id;
        _store.Update(rec);
        return Ok(new { success = true, id });
    }

    /// <summary>复现两联动：次数 +1、复现时间=当前时间（不影响状态）；body.VehicleCode 覆盖车号（空=清空）。</summary>
    [HttpPost("{id:long}/reproduce")]
    public ActionResult<object> Reproduce(long id, [FromBody] ExceptionRecordReproduceRequest req)
    {
        _store.Reproduce(id, req?.VehicleCode);
        return Ok(new { success = true, id });
    }

    [HttpDelete("{id:long}")]
    public ActionResult<object> Delete(long id, [FromQuery] string? password = null)
    {
        if (password != "wayzim") return StatusCode(403, new { success = false, message = "删除密码错误" });
        _store.Remove(id);
        return Ok(new { success = true, id });
    }
}