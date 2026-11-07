using Backend.Shared.Infrastructure.Repository;
using Contracts.Entities;

namespace WCSBackend.Modules.Wcs.Infrastructure;

public sealed class ProjectWorkspaceStore
{
    private readonly IUnitOfWorkFactory _uow;
    private static readonly (string Key, string Title)[] Sections =
    [
        ("overview", "项目概览"),
        ("risks", "项目风险"),
        ("deployment", "部署文档"),
        ("data", "项目数据"),
        ("owners", "项目责任人"),
        ("notes", "补充说明"),
    ];

    public ProjectWorkspaceStore(IUnitOfWorkFactory uow) => _uow = uow;

    public List<ProjectWorkspaceDto> GetProjects()
    {
        using var uow = _uow.Create();
        return uow.Repository<ProjectWorkspaceDto>().Query()
            .OrderBy(x => x.Name)
            .ToList();
    }

    public ProjectWorkspaceDto? GetProject(string name)
    {
        using var uow = _uow.Create();
        return uow.Repository<ProjectWorkspaceDto>().Query().FirstOrDefault(x => x.Name == name);
    }

    public ProjectWorkspaceDto CreateProject(string name)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var uow = _uow.Create();
        var projects = uow.Repository<ProjectWorkspaceDto>();
        var existing = projects.Query().FirstOrDefault(x => x.Name == name);
        if (existing != null) return existing;

        var project = new ProjectWorkspaceDto { Name = name, CreatedAt = now, UpdatedAt = now };
        projects.AddAsync(project).GetAwaiter().GetResult();
        var sections = uow.Repository<ProjectSectionDto>();
        foreach (var (key, title) in Sections)
            sections.AddAsync(new ProjectSectionDto { ProjectName = name, Key = key, Title = title, UpdatedAt = now })
                .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        return project;
    }

    public List<ProjectSectionDto> GetSections(string projectName)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<ProjectSectionDto>();
        var rows = repo.Query().Where(x => x.ProjectName == projectName).ToList();
        var missing = Sections.Where(def => rows.All(row => row.Key != def.Key)).ToList();
        if (missing.Count > 0)
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var (key, title) in missing)
                repo.AddAsync(new ProjectSectionDto { ProjectName = projectName, Key = key, Title = title, UpdatedAt = now })
                    .GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
            rows = repo.Query().Where(x => x.ProjectName == projectName).ToList();
        }
        return rows.OrderBy(x => Array.FindIndex(Sections, def => def.Key == x.Key)).ToList();
    }

    public ProjectSectionDto? SaveSection(string projectName, string key, string markdown)
    {
        var definition = Sections.FirstOrDefault(x => x.Key == key);
        if (definition == default) return null;
        using var uow = _uow.Create();
        var repo = uow.Repository<ProjectSectionDto>();
        var row = repo.Query().FirstOrDefault(x => x.ProjectName == projectName && x.Key == key);
        if (row == null)
        {
            row = new ProjectSectionDto { ProjectName = projectName, Key = key, Title = definition.Title };
            repo.AddAsync(row).GetAwaiter().GetResult();
        }
        row.Title = definition.Title;
        row.MarkdownContent = markdown ?? "";
        row.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        uow.CommitAsync().GetAwaiter().GetResult();
        return row;
    }

    public void RemoveProject(string name)
    {
        using var uow = _uow.Create();
        uow.Repository<ProjectSectionDto>().DeleteWhereAsync(x => x.ProjectName == name).GetAwaiter().GetResult();
        uow.Repository<ProjectWorkspaceDto>().DeleteWhereAsync(x => x.Name == name).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public bool RenameProject(string currentName, string newName)
    {
        using var uow = _uow.Create();
        var projects = uow.Repository<ProjectWorkspaceDto>();
        var project = projects.Query().FirstOrDefault(x => x.Name == currentName);
        if (project == null) return false;

        var sections = uow.Repository<ProjectSectionDto>();
        var oldSections = sections.Query().Where(x => x.ProjectName == currentName).ToList();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        projects.DeleteWhereAsync(x => x.Name == currentName).GetAwaiter().GetResult();
        sections.DeleteWhereAsync(x => x.ProjectName == currentName).GetAwaiter().GetResult();
        projects.AddAsync(new ProjectWorkspaceDto { Name = newName, CreatedAt = project.CreatedAt, UpdatedAt = now })
            .GetAwaiter().GetResult();
        foreach (var section in oldSections)
            sections.AddAsync(new ProjectSectionDto
            {
                ProjectName = newName,
                Key = section.Key,
                Title = section.Title,
                MarkdownContent = section.MarkdownContent,
                UpdatedAt = section.UpdatedAt
            }).GetAwaiter().GetResult();

        uow.CommitAsync().GetAwaiter().GetResult();
        return true;
    }
}
