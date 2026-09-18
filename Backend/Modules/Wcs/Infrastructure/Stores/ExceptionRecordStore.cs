using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class ExceptionRecordStore
{
    private readonly IUnitOfWorkFactory _uow;

    public ExceptionRecordStore(IUnitOfWorkFactory uowFactory) => _uow = uowFactory;

    public long Add(Entities.ExceptionRecordDto rec)
    {
        rec.CreatedAt = DateTime.Now.ToString("O");
        rec.UpdatedAt = DateTime.Now.ToString("O");
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ExceptionRecordDto>();
        repo.AddAsync(rec).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
        return rec.Id;
    }

    public List<Entities.ExceptionRecordDto> GetAll(string? vehicle = null, string? dateFrom = null, string? dateTo = null, string? status = null, string? dept = null, string? project = null)
    {
        using var uow = _uow.Create();
        var q = uow.Repository<Entities.ExceptionRecordDto>().Query();
        if (!string.IsNullOrEmpty(vehicle)) q = q.Where(r => r.VehicleCode == vehicle);
        if (!string.IsNullOrEmpty(dateFrom)) q = q.Where(r => string.Compare(r.HappenedAt, dateFrom) >= 0);
        if (!string.IsNullOrEmpty(dateTo)) q = q.Where(r => string.Compare(r.HappenedAt, dateTo) <= 0);
        if (!string.IsNullOrEmpty(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = q.Where(r => statuses.Contains(r.Status));
        }
        if (!string.IsNullOrEmpty(dept)) q = q.Where(r => r.ResponsibleDept == dept);
        if (!string.IsNullOrEmpty(project)) q = q.Where(r => r.Project == project);
        return q.OrderByDescending(r => r.Id).ToList();
    }

    /// <summary>项目名去重列表（含空串=未分类）。</summary>
    public List<string> Projects()
    {
        using var uow = _uow.Create();
        return uow.Repository<Entities.ExceptionRecordDto>().Query()
            .Select(r => r.Project).Distinct().ToList();
    }

    /// <summary>删除某项目下的全部记录。</summary>
    public void RemoveByProject(string project)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ExceptionRecordDto>().DeleteWhereAsync(r => r.Project == project)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Update(Entities.ExceptionRecordDto rec)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ExceptionRecordDto>();
        var row = repo.FindAsync(rec.Id).GetAwaiter().GetResult();
        if (row == null) return;
        row.HappenedAt = rec.HappenedAt;
        row.VehicleCode = rec.VehicleCode;
        row.Phenomenon = rec.Phenomenon;
        row.Reason = rec.Reason;
        row.Progress = rec.Progress;
        row.ResponsibleDept = rec.ResponsibleDept;
        row.Status = rec.Status;
        row.Project = rec.Project;
        row.ReproducedAt = rec.ReproducedAt;
        row.ReproduceCount = rec.ReproduceCount;
        row.UpdatedAt = DateTime.Now.ToString("O");
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    /// <summary>复现三联动：次数 +1、复现时间=当前、置为未解决；车号追加（顿号分隔，去重）。</summary>
    public void Reproduce(long id, string? vehicleCode)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<Entities.ExceptionRecordDto>();
        var row = repo.FindAsync(id).GetAwaiter().GetResult();
        if (row == null) return;
        if (!string.IsNullOrEmpty(vehicleCode))
        {
            var existing = row.VehicleCode ?? "";
            if (existing.Length == 0) row.VehicleCode = vehicleCode;
            else if (!existing.Contains(vehicleCode, StringComparison.Ordinal))
                row.VehicleCode = existing + "、" + vehicleCode;
        }
        else row.VehicleCode = "";
        row.ReproduceCount += 1;
        row.ReproducedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        row.Status = "pending";
        row.UpdatedAt = DateTime.Now.ToString("O");
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public void Remove(long id)
    {
        using var uow = _uow.Create();
        uow.Repository<Entities.ExceptionRecordDto>().DeleteWhereAsync(r => r.Id == id)
            .GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }
}

/// <summary>项目记录（SQLite project_logs 表，纯 HTTP 读写）。Singleton。</summary>
