namespace Backend.Shared;

/// <summary>共享数据库运行配置。系统统一使用 MySQL。</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string MySqlConnectionStringName { get; set; } = "Database";
    public string MySqlServerVersion { get; set; } = "8.0.13";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MySqlConnectionStringName))
            throw new InvalidOperationException("Database:MySqlConnectionStringName 不能为空。");
        if (!Version.TryParse(MySqlServerVersion, out _))
            throw new InvalidOperationException($"Database:MySqlServerVersion 格式无效：{MySqlServerVersion}");
    }
}

public sealed record SharedDatabaseRegistration(
    string DefaultMySqlConnectionStringName,
    System.Reflection.Assembly MigrationsAssembly);
