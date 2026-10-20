using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using WCSBackend.Modules.CommonTools.Services;

namespace WCSBackend.Modules.CommonTools.Api;

[ApiController]
[Route("api/common-tools/database-scripts")]
public sealed class DatabaseScriptsController(CommonToolsDatabaseConfigStore configStore) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var config = await configStore.GetAsync(cancellationToken);
        return Ok(new { config.Configured, config.Server, config.Port, config.Database, config.Username, config.HasPassword,
            message = config.Configured ? "CommonTools 数据库连接配置已保存。" : "请填写 CommonTools 使用的 rcs 数据库连接配置。" });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] SaveDatabaseConfigRequest request, CancellationToken cancellationToken)
    {
        try { await configStore.SaveAsync(request, cancellationToken); return Ok(new { success = true, message = "数据库连接配置已加密保存。" }); }
        catch (Exception ex) { return BadRequest(new { success = false, message = SafeMessage(ex) }); }
    }

    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = await configStore.GetConnectionStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("请先保存数据库连接配置。");
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return Ok(new { success = true, message = "数据库连接成功。" });
        }
        catch (Exception ex) { return BadRequest(new { success = false, message = SafeMessage(ex) }); }
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        InvalidOperationException => exception.Message,
        MySqlException mysql => $"数据库操作失败（MySQL {mysql.Number}）：{mysql.Message}",
        _ => "数据库操作失败，请检查连接配置和数据库权限。"
    };
}
