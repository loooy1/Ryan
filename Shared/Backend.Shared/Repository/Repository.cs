using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Backend.Shared.Infrastructure.Repository;

/// <summary>泛型仓储实现：操作同一 UnitOfWork 持有的 DbContext 实例（变更由 CommitAsync 统一提交）。</summary>
public class Repository<TEntity> : IRepository<TEntity> where TEntity : class
{
    private readonly GrcsDbContext _db;
    private readonly DbSet<TEntity> _set;

    public Repository(GrcsDbContext db)
    {
        _db = db;
        _set = db.Set<TEntity>();
    }

    public Task AddAsync(TEntity entity) => _set.AddAsync(entity).AsTask();

    public void Remove(TEntity entity) => _set.Remove(entity);

    public Task<int> DeleteWhereAsync(Expression<Func<TEntity, bool>> predicate)
        => _set.Where(predicate).ExecuteDeleteAsync();

    public Task<List<TEntity>> FindAllAsync() => _set.AsNoTracking().ToListAsync();

    public Task<List<TEntity>> FindAllAsync(Expression<Func<TEntity, bool>> predicate)
        => _set.AsNoTracking().Where(predicate).ToListAsync();

    /// <summary>按主键查找；主键类型自适应（Guid/int/long/string，从 EF 模型读取实际类型再转换）。</summary>
    public async Task<TEntity?> FindAsync(object id)
    {
        var keyType = _db.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()?.Properties.FirstOrDefault()?.ClrType;
        if (keyType == typeof(Guid)) return await _set.FindAsync(Guid.Parse(id.ToString()!));
        if (keyType == typeof(int)) return await _set.FindAsync(Convert.ToInt32(id));
        if (keyType == typeof(long)) return await _set.FindAsync(Convert.ToInt64(id));
        return await _set.FindAsync(id);
    }

    public IQueryable<TEntity> Query() => _set.AsQueryable();
}