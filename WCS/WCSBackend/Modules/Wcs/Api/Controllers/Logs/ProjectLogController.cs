using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using Contracts.Entities;
using Microsoft.AspNetCore.Mvc;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 项目记录接口（/api/wcs/project-logs）：每日项目日程台账。
/// 纯 HTTP（前端推给后端 / 从后端读取），无轮询无 SignalR。
/// GET 列表（日期倒序）；POST 新增；PUT 更新；DELETE 删除。
/// </summary>
[ApiController]
[Route("api/wcs/project-logs")]
public class ProjectLogController : ControllerBase
{
    private readonly ProjectLogStore _store;

    public ProjectLogController(ProjectLogStore store) => _store = store;

    /// <summary>查询参数：dateFrom/dateTo=所属日期(yyyy-MM-dd)；status=状态（pending/done/cancelled，单选）；project=项目名（空串=仅未分类）。</summary>
    [HttpGet]
    public ActionResult<object> GetAll(string? dateFrom = null, string? dateTo = null, string? status = null, string? project = null)
        => Ok(_store.GetAll(dateFrom, dateTo, status, project));

    /// <summary>项目名去重列表（含空串=未分类），供前端下拉切换。</summary>
    [HttpGet("projects")]
    public ActionResult<object> Projects() => Ok(_store.Projects());

    /// <summary>删除某项目及其全部项目记录（需密码，不可恢复）。</summary>
    [HttpDelete("projects/{name}")]
    public ActionResult<object> DeleteProject(string name, [FromQuery] string? password = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { success = false, message = "项目名不能为空" });
        if (password != "wayzim") return StatusCode(403, new { success = false, message = "删除密码错误" });
        _store.RemoveByProject(name.Trim());
        return Ok(new { success = true });
    }

    [HttpPost]
    public ActionResult<object> Add([FromBody] ProjectLogDto rec)
    {
        if (rec == null) return BadRequest(new { success = false, message = "请求体不能为空" });
        if (string.IsNullOrWhiteSpace(rec.LogDate)) return BadRequest(new { success = false, message = "日期不能为空" });
        if (string.IsNullOrWhiteSpace(rec.Content)) return BadRequest(new { success = false, message = "日程内容不能为空" });
        var id = _store.Add(rec);
        return Ok(new { success = true, id });
    }

    [HttpPut("{id:long}")]
    public ActionResult<object> Update(long id, [FromBody] ProjectLogDto rec)
    {
        if (rec == null) return BadRequest(new { success = false, message = "请求体不能为空" });
        if (string.IsNullOrWhiteSpace(rec.LogDate)) return BadRequest(new { success = false, message = "日期不能为空" });
        if (string.IsNullOrWhiteSpace(rec.Content)) return BadRequest(new { success = false, message = "日程内容不能为空" });
        rec.Id = id;
        _store.Update(rec);
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