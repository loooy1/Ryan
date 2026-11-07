using Backend.Shared.Infrastructure.Repository;
using Contracts.Entities;
using Contracts.Dtos;
using Mapster;
using Microsoft.AspNetCore.Http;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class TaskTemplateStore
{
    private const string DefaultRcsType = "RCS1";
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();

    public TaskTemplateStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
    }

    public List<TaskTemplateDto> GetAll(string? profileId = null)
    {
        lock (_lock)
        {
            var rcsType = NormalizeProfileId(profileId);
            using var uow = _uow.Create();
            return uow.Repository<TaskTemplateRow>().Query()
                .Where(row => row.RcsType == rcsType)
                .OrderBy(row => row.UpdatedAt)
                .ToList()
                .Select(row => row.Adapt<TaskTemplateDto>())
                .ToList();
        }
    }

    public void ReplaceAll(IEnumerable<TaskTemplateDto> items, string? profileId = null)
    {
        lock (_lock)
        {
            var rcsType = NormalizeProfileId(profileId);
            var updated = items?.ToList() ?? [];
            using var uow = _uow.Create();
            var repo = uow.Repository<TaskTemplateRow>();
            repo.DeleteWhereAsync(row => row.RcsType == rcsType).GetAwaiter().GetResult();
            foreach (var dto in updated)
            {
                var row = dto.Adapt<TaskTemplateRow>();
                row.RcsType = rcsType;
                repo.AddAsync(row).GetAwaiter().GetResult();
            }
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    public bool Remove(string value, string? profileId = null)
    {
        lock (_lock)
        {
            var rcsType = NormalizeProfileId(profileId);
            using var uow = _uow.Create();
            var repo = uow.Repository<TaskTemplateRow>();
            if (!repo.Query().Any(x => x.RcsType == rcsType && x.Value == value)) return false;
            repo.DeleteWhereAsync(x => x.RcsType == rcsType && x.Value == value).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
            return true;
        }
    }

    private static string NormalizeProfileId(string? profileId)
        => string.IsNullOrWhiteSpace(profileId) ? DefaultRcsType : profileId.Trim();
}

/// <summary>
/// 功能模板存储（内存 + EF Core 持久化 feature_modules 表）。
/// 跨浏览器/换机共享：前端创建模块后 POST 到这里，其他页面/标签页/浏览器拉取同一份。
/// Singleton。
/// </summary>
