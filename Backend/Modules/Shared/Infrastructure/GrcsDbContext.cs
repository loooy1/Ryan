using Microsoft.EntityFrameworkCore;

namespace GrcsBackend.Modules.Shared.Infrastructure;

/// <summary>
/// 统一 EF Core 数据上下文（模块化单体形态）：
/// 实体与映射配置分散在各模块目录（Modules/Wcs/Infrastructure/{Entities,Configurations}、将来 Modules/Rcs/...），
/// 本类用 ApplyConfigurationsFromAssembly 反射扫描全程序集自动发现——新增业务表零注册、本类零改动。
/// 连接串：ContentRoot/grcs.db（唯一数据文件，全库 EF 访问）。
/// 生命周期：AddDbContextFactory 短生命周期模式（Singleton Store 注入 factory，每次操作 CreateDbContext）。
/// 访问方式：不声明 DbSet 属性，调用方用 context.Set&lt;T&gt;()（保持本类不依赖任何模块类型）。
/// 迁移约定：新表走 Add-Migration；接管现有表时实体配置 ExcludeFromMigrations() 防止重建冲突。
/// </summary>
public class GrcsDbContext : DbContext
{
    public GrcsDbContext(DbContextOptions<GrcsDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GrcsDbContext).Assembly);
    }
}