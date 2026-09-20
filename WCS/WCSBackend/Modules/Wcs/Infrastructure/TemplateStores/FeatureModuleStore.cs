using Backend.Shared.Infrastructure.Repository;
using Contracts.Entities;
using Contracts.Dtos;
using Mapster;
using Microsoft.AspNetCore.Http;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class FeatureModuleStore
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<FeatureModuleDto> _items = [];

    public FeatureModuleStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            using var uow = _uow.Create();
            _items = uow.Repository<FeatureModuleRow>().FindAllAsync().GetAwaiter().GetResult()
                .Select(r => r.Adapt<FeatureModuleDto>()).ToList();
        }
        catch { }
    }

    public List<FeatureModuleDto> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public void ReplaceAll(IEnumerable<FeatureModuleDto> items)
    {
        lock (_lock)
        {
            _items = items?.ToList() ?? [];
            using var uow = _uow.Create();
            var repo = uow.Repository<FeatureModuleRow>();
            repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
            foreach (var dto in _items)
                repo.AddAsync(dto.Adapt<FeatureModuleRow>()).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                using var uow = _uow.Create();
                uow.Repository<FeatureModuleRow>().DeleteWhereAsync(x => x.Id == id).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            return n > 0;
        }
    }
}

/// <summary>
/// 自动化模板存储（内存 + EF Core 持久化 auto_templates 表）。
/// 跨浏览器/换机共享：前端创建模板后 POST 到这里，其他页面/标签页/浏览器拉取同一份。
/// Singleton。
/// </summary>
