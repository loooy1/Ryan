using System.Reflection;
using Backend.Shared.Infrastructure;
using Backend.Shared.Infrastructure.Repository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backend.Shared;

/// <summary>
/// 共享基础设施模块（WCS/RCS 共用）：EF Core 数据上下文（IDbContextFactory 短生命周期模式，
/// 兼容 Singleton Store 注入；连接串指向 ContentRoot/{dbFileName}）
/// + 工作单元/泛型仓储基座（UnitOfWorkFactory 按需创建短生命周期 UoW）
/// + 实体配置程序集注册（WCS 传自身程序集、RCS 传自身程序集，各扫各的配置）。
/// migrationsAssembly：EF 迁移所在程序集（各应用程序集，如 typeof(Program).Assembly）——
/// DbContext 在共享类库，若不显式指定，EF 会去 Backend.Shared 里找迁移而找不到。
/// </summary>
public static class SharedModuleExtensions
{
    public static IServiceCollection AddSharedModule(this IServiceCollection services,
        string dbFileName, Assembly migrationsAssembly, params Assembly[] configAssemblies)
    {
        services.AddSingleton<IEnumerable<Assembly>>(configAssemblies);
        services.AddDbContextFactory<GrcsDbContext>((sp, options) =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(env.ContentRootPath, dbFileName),
                DefaultTimeout = 30
            }.ToString();
            options.UseSqlite(connStr, b => b.MigrationsAssembly(migrationsAssembly.GetName().Name));
        });
        services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        return services;
    }
}