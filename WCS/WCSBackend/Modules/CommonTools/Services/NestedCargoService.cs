using MySqlConnector;
using WCSBackend.Modules.CommonTools.Models;

namespace WCSBackend.Modules.CommonTools.Services;

/// <summary>
/// CommonTools 的真实 cargo 嵌套操作。只依赖独立数据库配置，不依赖 WCS/RCS 业务服务。
/// </summary>
public sealed class NestedCargoService(CommonToolsDatabaseConfigStore configStore)
{
    public async Task<NestedCargoResult> AddAsync(NestedCargoRequest request, CancellationToken cancellationToken)
    {
        var sceneName = Required(request.SceneName, "sceneName", 100);
        var rackCode = Required(request.RackCode, "rackCode", 100);
        var cargoCode = Required(request.CargoCode, "cargoCode", 100);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long rackId;
        await using (var rackCommand = new MySqlCommand("""
            SELECT id FROM cargo
            WHERE scene_name = @sceneName AND code = @rackCode
              AND cargo_type = 2 AND parent_cargo_id IS NULL
            FOR UPDATE
            """, connection, transaction))
        {
            rackCommand.Parameters.AddWithValue("@sceneName", sceneName);
            rackCommand.Parameters.AddWithValue("@rackCode", rackCode);
            var value = await rackCommand.ExecuteScalarAsync(cancellationToken);
            if (value is null || value is DBNull)
                throw new InvalidOperationException($"托盘架“{rackCode}”不存在、不是托盘架，或已经是嵌套货物。");
            rackId = Convert.ToInt64(value);
        }

        await using (var childCommand = new MySqlCommand(
            "SELECT EXISTS (SELECT 1 FROM cargo WHERE parent_cargo_id = @rackId)", connection, transaction))
        {
            childCommand.Parameters.AddWithValue("@rackId", rackId);
            if (Convert.ToInt32(await childCommand.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException($"托盘架“{rackCode}”已经存在嵌套货物，不能重复嵌套。");
        }

        await using (var duplicateCommand = new MySqlCommand("""
            SELECT EXISTS (SELECT 1 FROM cargo WHERE scene_name = @sceneName AND code = @cargoCode)
            """, connection, transaction))
        {
            duplicateCommand.Parameters.AddWithValue("@sceneName", sceneName);
            duplicateCommand.Parameters.AddWithValue("@cargoCode", cargoCode);
            if (Convert.ToInt32(await duplicateCommand.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException($"货物“{cargoCode}”已存在。");
        }

        long cargoId;
        await using (var insertCommand = new MySqlCommand("""
            INSERT INTO cargo
            (code, length, width, height, cargo_size, angle, heat, scene_name,
             home_station, home_cargo_area, cargo_type, parent_cargo_id)
            SELECT @cargoCode, rack.length, rack.width, rack.height, rack.cargo_size, 0, 0,
                   rack.scene_name, rack.home_station, rack.home_cargo_area, 1, rack.id
            FROM cargo rack WHERE rack.id = @rackId
            """, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("@cargoCode", cargoCode);
            insertCommand.Parameters.AddWithValue("@rackId", rackId);
            try
            {
                if (await insertCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("没有插入货物，请确认托盘架状态未发生变化。");
                cargoId = checked((long)insertCommand.LastInsertedId);
            }
            catch (MySqlException ex) when (ex.Number == 1062)
            {
                throw new InvalidOperationException($"货物“{cargoCode}”已存在。");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new NestedCargoResult(rackId, cargoId, sceneName, rackCode, cargoCode);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connectionString = await configStore.GetConnectionStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("请先在 CommonTools 的数据库配置页面保存 rcs 数据库连接。");
        var connection = new MySqlConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static string Required(string? value, string name, int maxLength)
    {
        var result = value?.Trim();
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException($"{name} 不能为空。");
        if (result.Length > maxLength) throw new InvalidOperationException($"{name} 长度不能超过 {maxLength} 个字符。");
        return result;
    }
}
