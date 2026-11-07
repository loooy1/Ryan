using Contracts.Entities;
using Microsoft.AspNetCore.Mvc;
using WCSBackend.Modules.Wcs.Infrastructure;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

[ApiController]
[Route("api/wcs/projects")]
public sealed class ProjectWorkspaceController : ControllerBase
{
    private readonly ProjectWorkspaceStore _projects;
    private readonly ProjectLogStore _logs;

    public ProjectWorkspaceController(ProjectWorkspaceStore projects, ProjectLogStore logs)
    {
        _projects = projects;
        _logs = logs;
    }

    [HttpGet]
    public ActionResult<List<ProjectWorkspaceDto>> GetProjects() => Ok(_projects.GetProjects());

    [HttpPost]
    public ActionResult<object> Create([FromBody] CreateProjectRequest request)
    {
        var name = request?.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { success = false, message = "项目名称不能为空" });
        if (name.Length > 191) return BadRequest(new { success = false, message = "项目名称不能超过 191 个字符" });
        if (_projects.GetProject(name) != null) return Conflict(new { success = false, message = "项目已存在" });
        return Ok(new { success = true, data = _projects.CreateProject(name) });
    }

    [HttpPut("{name}")]
    public ActionResult<object> Rename(string name, [FromBody] RenameProjectRequest request)
    {
        var currentName = name?.Trim() ?? "";
        var newName = request?.NewName?.Trim() ?? "";
        if (currentName.Length == 0) return BadRequest(new { success = false, message = "当前项目名不能为空" });
        if (newName.Length == 0) return BadRequest(new { success = false, message = "新项目名不能为空" });
        if (newName.Length > 191) return BadRequest(new { success = false, message = "项目名称不能超过 191 个字符" });
        if (request?.Password != "wayzim") return StatusCode(403, new { success = false, message = "密码错误" });
        if (_projects.GetProject(currentName) == null) return NotFound(new { success = false, message = "项目不存在" });
        if (string.Equals(currentName, newName, StringComparison.Ordinal)) return Ok(new { success = true, name = newName });
        if (_projects.GetProject(newName) != null) return Conflict(new { success = false, message = "目标项目名称已存在" });

        if (!_projects.RenameProject(currentName, newName)) return NotFound(new { success = false, message = "项目不存在" });
        _logs.RenameByProject(currentName, newName);
        return Ok(new { success = true, name = newName });
    }

    [HttpGet("{name}/sections")]
    public ActionResult<List<ProjectSectionDto>> GetSections(string name)
    {
        var projectName = name.Trim();
        if (_projects.GetProject(projectName) == null) return NotFound(new { success = false, message = "项目不存在" });
        return Ok(_projects.GetSections(projectName));
    }

    [HttpPut("{name}/sections/{key}")]
    public ActionResult<object> SaveSection(string name, string key, [FromBody] SaveProjectSectionRequest request)
    {
        var projectName = name.Trim();
        if (_projects.GetProject(projectName) == null) return NotFound(new { success = false, message = "项目不存在" });
        var row = _projects.SaveSection(projectName, key, request?.MarkdownContent ?? "");
        return row == null
            ? NotFound(new { success = false, message = "资料分区不存在" })
            : Ok(new { success = true, data = row });
    }

    [HttpDelete("{name}")]
    public ActionResult<object> Delete(string name, [FromQuery] string? password = null)
    {
        var projectName = name.Trim();
        if (projectName.Length == 0) return BadRequest(new { success = false, message = "项目名称不能为空" });
        if (password != "wayzim") return StatusCode(403, new { success = false, message = "删除密码错误" });
        _projects.RemoveProject(projectName);
        _logs.RemoveByProject(projectName);
        return Ok(new { success = true });
    }

    public sealed class CreateProjectRequest
    {
        public string Name { get; set; } = "";
    }

    public sealed class RenameProjectRequest
    {
        public string NewName { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public sealed class SaveProjectSectionRequest
    {
        public string MarkdownContent { get; set; } = "";
    }
}
