using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Backend.Shared.Infrastructure;

/// <summary>
/// 统一 EF Core 数据上下文（WCS/RCS 共用底座）：
/// 实体与映射配置分散在各应用模块目录（如 Backend/Modules/Wcs/Infrastructure/{Entities,Configurations}、
/// RcsBackend/Modules/Rcs/...），本类用 configAssemblies 反射扫描各应用程序集自动发现——
/// 新增业务表零注册、本类零改动（WCS 传 WCSBackend 程序集、RCS 传 RcsBackend 程序集）。
/// 连接串：由 AddSharedModule(dbFileName) 指定（grcs.db / rcs.db，唯一数据文件，全库 EF 访问）。
/// 生命周期：AddDbContextFactory 短生命周期模式（Singleton Store 注入 factory，每次操作 CreateDbContext）。
/// 访问方式：不声明 DbSet 属性，调用方用 context.Set&lt;T&gt;()（保持本类不依赖任何模块类型）。
/// 迁移约定：各应用工程各自 Add-Migration（迁移快照按各自程序集配置生成） 。
/// </summary>
public class GrcsDbContext : DbContext
{
    private readonly IEnumerable<Assembly> _configAssemblies;

    public GrcsDbContext(DbContextOptions<GrcsDbContext> options, IEnumerable<Assembly> configAssemblies) : base(options)
    {
        _configAssemblies = configAssemblies;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        foreach (var asm in _configAssemblies)
            modelBuilder.ApplyConfigurationsFromAssembly(asm);
    }
}