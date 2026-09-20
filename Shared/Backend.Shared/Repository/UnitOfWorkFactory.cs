using Microsoft.EntityFrameworkCore;

namespace Backend.Shared.Infrastructure.Repository;

/// <summary>工作单元工厂实现：转接 DbContextFactory（每个 UoW 一个独立 DbContext，与 IDbContextFactory 短生命周期模式一致）。</summary>
public class UnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly IDbContextFactory<GrcsDbContext> _factory;

    public UnitOfWorkFactory(IDbContextFactory<GrcsDbContext> factory) => _factory = factory;

    public IUnitOfWork Create() => new UnitOfWork(_factory);
}