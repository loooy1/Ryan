using System.Reflection;
using Backend.Shared.Infrastructure;
using Backend.Shared.Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Shared;

/// <summary>
/// 共享基础设施模块（WCS/RCS 共用）：EF Core 数据上下文（IDbContextFactory 短生命周期模式，
/// 兼容 Singleton Store 注入；连接串指向 MySQL）
/// + 工作单元/泛型仓储基座（UnitOfWorkFactory 按需创建短生命周期 UoW）
/// + 实体配置程序集注册（WCS 传自身程序集、RCS 传自身程序集，各扫各的配置）。
/// migrationsAssembly：EF 迁移所在程序集——
/// DbContext 在共享类库，若不显式指定，EF 会去 Backend.Shared 里找迁移而找不到。
/// </summary>
public static class SharedModuleExtensions
{
    public static IServiceCollection AddSharedModule(this IServiceCollection services,
        IConfiguration configuration,
        SharedDatabaseRegistration registration,
        params Assembly[] configAssemblies)
    {
        var database = new DatabaseOptions
        {
            MySqlConnectionStringName = registration.DefaultMySqlConnectionStringName
        };
        configuration.GetSection(DatabaseOptions.SectionName).Bind(database);
        database.Validate();

        services.AddSingleton(database);
        services.AddSingleton<IEnumerable<Assembly>>(configAssemblies);
        services.AddDbContextFactory<GrcsDbContext>((sp, options) =>
        {
            var mySqlConnection = configuration.GetConnectionString(database.MySqlConnectionStringName);
            if (string.IsNullOrWhiteSpace(mySqlConnection))
                throw new InvalidOperationException(
                    $"未配置 ConnectionStrings:{database.MySqlConnectionStringName}，无法连接 MySQL。");

            var serverVersion = new MySqlServerVersion(Version.Parse(database.MySqlServerVersion));
            options.UseMySql(mySqlConnection, serverVersion, b =>
            {
                b.MigrationsAssembly(registration.MigrationsAssembly.GetName().Name);
                b.EnableRetryOnFailure(5, TimeSpan.FromSeconds(2), null);
            });
        });
        services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        return services;
    }
}
