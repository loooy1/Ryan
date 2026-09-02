namespace GrcsBackend.Modules.Shared.Infrastructure.Repository;

/// <summary>
/// 工作单元（Unit of Work）：把一批仓储操作视为一个原子单元，最后 CommitAsync 一次性提交（同一事务）。
/// 持有一个独立的 DbContext 实例，用后必须 Dispose（using var 模式）。
/// 注意：本接口由 UnitOfWorkFactory.Create() 按需创建，不要注入到长寿命服务（Singleton）直接持有。
/// </summary>
public interface IUnitOfWork : IDisposable
{
    IRepository<TEntity> Repository<TEntity>() where TEntity : class;
    Task<int> CommitAsync();
}