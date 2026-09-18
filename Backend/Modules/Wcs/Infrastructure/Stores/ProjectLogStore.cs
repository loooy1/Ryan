using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class ProjectLogStore
{
    private readonly IUnitOfWorkFactory _uow;

    public ProjectLogStore(IUnitOfWorkFactory uowFactory) => _uow = uowFactory;

    public long Add(Entities.ProjectLogDto rec)
    {
        rec.CreatedAt = DateTime.Now.ToString("O");
        rec.UpdatedAt = DateTime.Now.ToString("O");
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ProjectLogDto>();
        repo.AddAsync(rec).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        return rec.Id;
    }

    public List<Entities.ProjectLogDto> GetAll(string? dateFrom = null, string? dateTo = null, string? status = null, string? project = null)
    {
        using var uow = _uow.Create();
        var q = uow.Repository<Entities.ProjectLogDto>().Query();
        if (!string.IsNullOrEmpty(dateFrom)) q = q.Where(r => string.Compare(r.LogDate, dateFrom) >= 0);
        if (!string.IsNullOrEmpty(dateTo)) q = q.Where(r => string.Compare(r.LogDate, dateTo) <= 0);
        if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrEmpty(project)) q = q.Where(r => r.Project == project);
        return q.OrderByDescending(r => r.Id).ToList();
    }

    /// <summary>项目名去重列表（含空串=未分类）。</summary>
    public List<string> Projects()
    {
        using var uow = _uow.Create();
        return uow.Repository<Entities.ProjectLogDto>().Query()
            .Select(r => r.Project).Distinct().ToList();
    }

    /// <summary>删除某项目下的全部记录。</summary>
    public void RemoveByProject(string project)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ProjectLogDto>().DeleteWhereAsync(r => r.Project == project)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Update(Entities.ProjectLogDto rec)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ProjectLogDto>();
        var row = repo.FindAsync(rec.Id).GetAwaiter().GetResult();
        if (row == null) return;
        row.LogDate = rec.LogDate;
        row.Content = rec.Content;
        row.Status = rec.Status;
        row.Project = rec.Project;
        row.Remark = rec.Remark;
        row.UpdatedAt = DateTime.Now.ToString("O");
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Remove(long id)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ProjectLogDto>().DeleteWhereAsync(r => r.Id == id)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }
}
