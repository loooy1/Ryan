using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Backend.Shared.Infrastructure;

namespace WCSBackend;

/// <summary>供 dotnet-ef 生成和更新 WCS 的 MySQL 迁移。</summary>
public sealed class WcsMySqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<GrcsDbContext>
{
    public GrcsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("WCS_MYSQL_CONNECTION")
            ?? "Server=127.0.0.1;Port=3306;Database=wcs_db;User ID=root;Password=unused";
        var serverVersionText = Environment.GetEnvironmentVariable("WCS_MYSQL_SERVER_VERSION") ?? "8.0.13";

        var options = new DbContextOptionsBuilder<GrcsDbContext>()
            .UseMySql(
                connectionString,
                new MySqlServerVersion(Version.Parse(serverVersionText)),
                mysql => mysql.MigrationsAssembly(typeof(WcsMySqlDesignTimeDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new GrcsDbContext(options, new[] { typeof(WcsMySqlDesignTimeDbContextFactory).Assembly });
    }
}
