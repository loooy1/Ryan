using Microsoft.Data.Sqlite;

namespace Backend.Shared;

/// <summary>SQLite WAL 模式：读写互不阻塞，写冲突排队（DefaultTimeout=30），解决并发写 "database is locked"。</summary>
public static class SqliteWal
{
    public static void EnsureWal(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Default Timeout=30");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}