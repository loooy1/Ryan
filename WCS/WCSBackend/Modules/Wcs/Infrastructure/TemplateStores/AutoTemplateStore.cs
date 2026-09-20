using Backend.Shared.Infrastructure.Repository;
using Contracts.Entities;
using Contracts.Dtos;
using Mapster;
using Microsoft.AspNetCore.Http;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class AutoTemplateStore
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<AutoTemplateDto> _items = [];

    public AutoTemplateStore(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            using var uow = _uow.Create();
            _items = uow.Repository<AutoTemplateRow>().FindAllAsync().GetAwaiter().GetResult()
                .Select(r => r.Adapt<AutoTemplateDto>()).ToList();
        }
        catch { }
    }

    public List<AutoTemplateDto> GetAll()
    {
        lock (_lock) return _items.ToList();
    }

    public void ReplaceAll(IEnumerable<AutoTemplateDto> items)
    {
        lock (_lock)
        {
            _items = items?.ToList() ?? [];
            using var uow = _uow.Create();
            var repo = uow.Repository<AutoTemplateRow>();
            repo.DeleteWhereAsync(_ => true).GetAwaiter().GetResult();
            foreach (var dto in _items)
                repo.AddAsync(dto.Adapt<AutoTemplateRow>()).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>单条保存：替换内存中同 Id 项（不存在则追加）并持久化，不影响其他模板。</summary>
    public void Upsert(AutoTemplateDto item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) return;
        lock (_lock)
        {
            var idx = _items.FindIndex(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _items[idx] = item; else _items.Add(item);
            using var uow = _uow.Create();
            var repo = uow.Repository<AutoTemplateRow>();
            var row = repo.FindAsync(item.Id).GetAwaiter().GetResult();
            if (row == null)
                repo.AddAsync(item.Adapt<AutoTemplateRow>()).GetAwaiter().GetResult();
            else
                item.Adapt(row);
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
                uow.Repository<AutoTemplateRow>().DeleteWhereAsync(x => x.Id == id).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
            }
            return n > 0;
        }
    }
}

/// <summary>
/// 通用 Mock 规则存储（内存 + EF Core 持久化 mock_rules 表）。
/// </summary>
