using Backend.Shared.Infrastructure.Repository;
using Contracts.Entities;
using Contracts.Dtos;
using Mapster;
using Microsoft.AspNetCore.Http;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class TaskTemplateStore
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<TaskTemplateDto> _items = [];

    public TaskTemplateStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            using var uow = _uow.Create();
            _items = uow.Repository<TaskTemplateRow>().FindAllAsync().GetAwaiter().GetResult()
                .Select(r => r.Adapt<TaskTemplateDto>()).ToList();
        }
        catch { }
    }

    public List<TaskTemplateDto> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public void ReplaceAll(IEnumerable<TaskTemplateDto> items)
    {
        lock (_lock)
        {
            _items = items?.ToList() ?? [];
            using var uow = _uow.Create();
            var repo = uow.Repository<TaskTemplateRow>();
            repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
            foreach (var dto in _items)
                repo.AddAsync(dto.Adapt<TaskTemplateRow>()).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    public bool Remove(string value)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                using var uow = _uow.Create();
                uow.Repository<TaskTemplateRow>().DeleteWhereAsync(x => x.Value == value).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            return n > 0;
        }
    }
}

/// <summary>
/// 功能模板存储（内存 + EF Core 持久化 feature_modules 表）。
/// 跨浏览器/换机共享：前端创建模块后 POST 到这里，其他页面/标签页/浏览器拉取同一份。
/// Singleton。
/// </summary>
