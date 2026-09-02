using System.Collections;
using GrcsBackend.Modules.Shared.Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;

namespace GrcsBackend.Modules.Shared.Infrastructure.Repository;

/// <summary>
/// 工作单元实现：从 DbContextFactory 创建独立 DbContext；Repository&lt;T&gt; 按类型按需创建并缓存复用。
/// 使用方式：using var uow = factory.Create(); ...; await uow.CommitAsync();
/// </summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly GrcsDbContext _db;
    private Hashtable? _repositories;

    public UnitOfWork(IDbContextFactory<GrcsDbContext> factory)
    {
        _db = factory.CreateDbContext();
    }

    public IRepository<TEntity> Repository<TEntity>() where TEntity : class
    {
        _repositories ??= new Hashtable();
        if (!_repositories.ContainsKey(typeof(TEntity).Name))
        {
            _repositories[typeof(TEntity).Name] = new Repository<TEntity>(_db);
        }
        return (IRepository<TEntity>)_repositories[typeof(TEntity).Name]!;
    }

    public Task<int> CommitAsync() => _db.SaveChangesAsync();

    public void Dispose() => _db.Dispose();
}