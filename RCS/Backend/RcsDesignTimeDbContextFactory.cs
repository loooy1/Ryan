using Backend.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using RCSBackend.Modules.Rcs;

namespace RCSBackend;

/// <summary>EF 迁移设计时上下文；只提供模型配置，不打开数据库连接。</summary>
public sealed class RcsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<GrcsDbContext>
{
    public GrcsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GrcsDbContext>()
            .UseMySql("Server=localhost;Port=3306;Database=rcs_migrations;User ID=design_time;Password=design_time",
                new MySqlServerVersion(new Version(8, 0, 13)), builder => builder.MigrationsAssembly(typeof(RcsModuleExtensions).Assembly.GetName().Name))
            .Options;
        return new GrcsDbContext(options, [typeof(RcsModuleExtensions).Assembly]);
    }
}
