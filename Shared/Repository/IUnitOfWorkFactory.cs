namespace Backend.Shared.Infrastructure.Repository;

/// <summary>工作单元工厂：长寿命服务（Singleton Store/后台服务）注入此工厂，每次操作 Create() 短生命周期 UoW。</summary>
public interface IUnitOfWorkFactory
{
    IUnitOfWork Create();
}