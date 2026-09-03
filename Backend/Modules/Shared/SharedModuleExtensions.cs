using GrcsBackend.Modules.Shared.Infrastructure;
using GrcsBackend.Modules.Shared.Infrastructure.Repository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrcsBackend.Modules.Shared;

/// <summary>
/// 共享基础设施模块：EF Core 数据上下文（IDbContextFactory 短生命周期模式，
/// 兼容 Singleton Store 注入；连接串指向 ContentRoot/grcs.db）
/// + 工作单元/泛型仓储基座（UnitOfWorkFactory 按需创建短生命周期 UoW）。
/// </summary>
public static class SharedModuleExtensions
{
    public static IServiceCollection AddSharedModule(this IServiceCollection services)
    {
        services.AddDbContextFactory<GrcsDbContext>((sp, options) =>
        {
            var env = sp.GetRequiredService<IWebHostEnvironment>();
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(env.ContentRootPath, "grcs.db"),
                DefaultTimeout = 30
            }.ToString();
            options.UseSqlite(connStr);
        });
        services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        return services;
    }
}