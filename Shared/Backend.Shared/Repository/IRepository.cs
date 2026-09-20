using System.Linq.Expressions;

namespace Backend.Shared.Infrastructure.Repository;

/// <summary>
/// 泛型仓储接口：通用 CRUD 基座（借鉴 GRCS 的 IDbContext/Repository 形态，去掉 IAggregateRoot 约束，保持轻量）。
/// 每个聚合/表一套通用操作；Store 特有的筛选逻辑用 Query()/FindAllAsync(predicate) 自行组合。
/// </summary>
public interface IRepository<TEntity> where TEntity : class
{
    Task AddAsync(TEntity entity);
    void Remove(TEntity entity);
    Task<int> DeleteWhereAsync(Expression<Func<TEntity, bool>> predicate);
    Task<List<TEntity>> FindAllAsync();
    Task<List<TEntity>> FindAllAsync(Expression<Func<TEntity, bool>> predicate);
    Task<TEntity?> FindAsync(object id);
    IQueryable<TEntity> Query();
}