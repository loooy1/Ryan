using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;

namespace WCSBackend.Modules.CommonTools.Services;

public sealed class CommonToolsDatabaseConfigStore(IDataProtectionProvider protectionProvider, IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("WCSBackend.CommonTools.Database.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = GetConfigPath(environment);
    private StoredDemoDatabaseConfig? _cached;

    public bool IsConfigured
    {
        get
        {
            var config = LoadCachedOrFile();
            return !string.IsNullOrWhiteSpace(config?.Server)
                && config.Port is > 0 and <= 65535
                && !string.IsNullOrWhiteSpace(config.Database)
                && !string.IsNullOrWhiteSpace(config.Username);
        }
    }

    public async Task<DemoDatabaseConfigView> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadAsync(cancellationToken);
            return new DemoDatabaseConfigView
            {
                Server = config?.Server ?? "127.0.0.1",
                Port = config?.Port ?? 3306,
                Database = config?.Database ?? "rcs",
                Username = config?.Username ?? "",
                HasPassword = !string.IsNullOrEmpty(config?.ProtectedPassword),
                Configured = IsValid(config)
            };
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(SaveDatabaseConfigRequest request, CancellationToken cancellationToken)
    {
        var server = request.Server?.Trim();
        var database = request.Database?.Trim();
        var username = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(server)) throw new InvalidOperationException("数据库主机不能为空。");
        if (server.Length > 255 || server.Contains(';') || server.Contains('"')) throw new InvalidOperationException("数据库主机格式无效。");
        if (request.Port is < 1 or > 65535) throw new InvalidOperationException("端口必须在 1 到 65535 之间。");
        if (string.IsNullOrWhiteSpace(database) || database.Length > 64) throw new InvalidOperationException("数据库 schema 名称不能为空，且不能超过 64 个字符。");
        if (string.IsNullOrWhiteSpace(username) || username.Length > 128) throw new InvalidOperationException("数据库用户名不能为空，且不能超过 128 个字符。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var old = await ReadAsync(cancellationToken);
            var password = string.IsNullOrEmpty(request.Password)
                ? old?.ProtectedPassword ?? ""
                : _protector.Protect(request.Password);
            var next = new StoredDemoDatabaseConfig(server, request.Port, database, username, password);
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var tempPath = _path + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(next, JsonOptions), cancellationToken);
            File.Move(tempPath, _path, overwrite: true);
            _cached = next;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadAsync(cancellationToken);
            if (!IsValid(config)) return null;
            var builder = new MySqlConnectionStringBuilder
            {
                Server = config!.Server,
                Port = (uint)config.Port,
                Database = config.Database,
                UserID = config.Username,
                SslMode = MySqlSslMode.Preferred,
                ConnectionTimeout = 5,
                DefaultCommandTimeout = 10,
                AllowUserVariables = false,
                CharacterSet = "utf8mb4"
            };
            if (!string.IsNullOrEmpty(config.ProtectedPassword))
                builder.Password = _protector.Unprotect(config.ProtectedPassword);
            return builder.ConnectionString;
        }
        finally { _gate.Release(); }
    }

    private async Task<StoredDemoDatabaseConfig?> ReadAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null) return _cached;
        if (!File.Exists(_path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            _cached = JsonSerializer.Deserialize<StoredDemoDatabaseConfig>(json, JsonOptions);
            return _cached;
        }
        catch (JsonException) { throw new InvalidOperationException("数据库配置文件无法读取，请在页面重新保存连接配置。"); }
    }

    private StoredDemoDatabaseConfig? LoadCachedOrFile()
    {
        if (_cached is not null) return _cached;
        if (!File.Exists(_path)) return null;
        try
        {
            _cached = JsonSerializer.Deserialize<StoredDemoDatabaseConfig>(File.ReadAllText(_path), JsonOptions);
            return _cached;
        }
        catch { return null; }
    }

    private static bool IsValid(StoredDemoDatabaseConfig? config)
        => !string.IsNullOrWhiteSpace(config?.Server)
            && config.Port is > 0 and <= 65535
            && !string.IsNullOrWhiteSpace(config.Database)
            && !string.IsNullOrWhiteSpace(config.Username);

    private static string GetConfigPath(IHostEnvironment environment)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = environment.ContentRootPath;
        return Path.Combine(root, "WCSBackend", "CommonTools", "database.json");
    }

    private sealed record StoredDemoDatabaseConfig(string Server, int Port, string Database, string Username, string ProtectedPassword);
}

public sealed class SaveDatabaseConfigRequest
{
    public string? Server { get; set; }
    public int Port { get; set; } = 3306;
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class DemoDatabaseConfigView
{
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public bool HasPassword { get; set; }
    public bool Configured { get; set; }
}
