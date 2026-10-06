using System.Data;
using System.Text.Json;
using Backend.Shared.Infrastructure;
using Contracts.Rcs.Inventory;
using Contracts.Rcs.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Api;

[ApiController]
[Route("api/rcs/inventory")]
public sealed class RcsInventoryController(IDbContextFactory<GrcsDbContext> factory) : ControllerBase
{
    [HttpGet("models")]
    public async Task<ActionResult<IReadOnlyList<RcsInventoryModelDto>>> GetModels(
        [FromQuery] string? itemType = null, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var query = db.Set<RcsInventoryModelRow>().AsNoTracking();
        if (IsItemType(itemType)) query = query.Where(x => x.ItemType == Normalize(itemType!));
        var rows = await query.OrderBy(x => x.ItemType).ThenBy(x => x.ModelCode).ToListAsync(token);
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("models")]
    public async Task<ActionResult<RcsInventoryModelDto>> CreateModel(
        [FromBody] RcsInventoryModelDto dto, CancellationToken token = default)
    {
        var error = ValidateModel(dto);
        if (error is not null) return BadRequest(RcsApiResponse.Rejected(error));
        await using var db = await factory.CreateDbContextAsync(token);
        var type = Normalize(dto.ItemType);
        var code = dto.ModelCode.Trim();
        if (await db.Set<RcsInventoryModelRow>().AnyAsync(x => x.ItemType == type && x.ModelCode == code, token))
            return Conflict(RcsApiResponse.Rejected($"{TypeName(type)}模型编码已存在：{code}"));
        var now = DateTime.UtcNow;
        var row = new RcsInventoryModelRow
        {
            ItemType = type, ModelCode = code, Name = dto.Name.Trim(), LengthMm = dto.LengthMm,
            WidthMm = dto.WidthMm, HeightMm = dto.HeightMm, WeightKg = dto.WeightKg,
            MaxLoadKg = type == RcsInventoryItemTypes.Pallet ? dto.MaxLoadKg : null,
            IsEnabled = dto.IsEnabled, MetadataJson = NormalizeJson(dto.MetadataJson), CreatedAt = now, UpdatedAt = now
        };
        db.Add(row);
        await db.SaveChangesAsync(token);
        return Created($"/api/rcs/inventory/models/{row.Id}", ToDto(row));
    }

    [HttpPut("models/{id:long}")]
    public async Task<ActionResult<RcsInventoryModelDto>> UpdateModel(long id,
        [FromBody] RcsInventoryModelDto dto, CancellationToken token = default)
    {
        var error = ValidateModel(dto);
        if (error is not null) return BadRequest(RcsApiResponse.Rejected(error));
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.Set<RcsInventoryModelRow>().FirstOrDefaultAsync(x => x.Id == id, token);
        if (row is null) return NotFound(RcsApiResponse.Rejected("模型不存在。"));
        var type = Normalize(dto.ItemType);
        var code = dto.ModelCode.Trim();
        if ((row.ItemType != type || row.ModelCode != code)
            && await db.Set<RcsInventoryInstanceRow>().AnyAsync(x => x.ItemType == row.ItemType && x.ModelCode == row.ModelCode, token))
            return Conflict(RcsApiResponse.Rejected("该模型已有实例在使用，不能修改类型或模型编码。"));
        if (await db.Set<RcsInventoryModelRow>().AnyAsync(x => x.Id != id && x.ItemType == type && x.ModelCode == code, token))
            return Conflict(RcsApiResponse.Rejected($"{TypeName(type)}模型编码已存在：{code}"));
        row.ItemType = type; row.ModelCode = code; row.Name = dto.Name.Trim();
        row.LengthMm = dto.LengthMm; row.WidthMm = dto.WidthMm; row.HeightMm = dto.HeightMm;
        row.WeightKg = dto.WeightKg; row.MaxLoadKg = type == RcsInventoryItemTypes.Pallet ? dto.MaxLoadKg : null;
        row.IsEnabled = dto.IsEnabled; row.MetadataJson = NormalizeJson(dto.MetadataJson); row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        return Ok(ToDto(row));
    }

    [HttpDelete("models/{id:long}")]
    public async Task<IActionResult> DeleteModel(long id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.Set<RcsInventoryModelRow>().FirstOrDefaultAsync(x => x.Id == id, token);
        if (row is null) return NotFound(RcsApiResponse.Rejected("模型不存在。"));
        if (await db.Set<RcsInventoryInstanceRow>().AnyAsync(x => x.ItemType == row.ItemType && x.ModelCode == row.ModelCode, token))
            return Conflict(RcsApiResponse.Rejected("该模型仍被实例使用，不能删除。"));
        db.Remove(row);
        await db.SaveChangesAsync(token);
        return Ok(RcsApiResponse.Accepted());
    }

    [HttpGet("instances")]
    public async Task<ActionResult<IReadOnlyList<RcsInventoryInstanceDto>>> GetInstances(
        [FromQuery] string? itemType = null, [FromQuery] string? mapCode = null,
        [FromQuery] string? pointCode = null, [FromQuery] string? parentInstanceCode = null,
        CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var query = db.Set<RcsInventoryInstanceRow>().AsNoTracking();
        if (IsItemType(itemType)) query = query.Where(x => x.ItemType == Normalize(itemType!));
        if (!string.IsNullOrWhiteSpace(mapCode)) query = query.Where(x => x.MapCode == mapCode);
        if (!string.IsNullOrWhiteSpace(pointCode)) query = query.Where(x => x.PointCode == pointCode);
        if (!string.IsNullOrWhiteSpace(parentInstanceCode)) query = query.Where(x => x.ParentInstanceCode == parentInstanceCode);
        var rows = await query.OrderBy(x => x.ItemType).ThenBy(x => x.InstanceCode).ToListAsync(token);
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("instances")]
    public async Task<ActionResult<RcsInventoryInstanceDto>> CreateInstance(
        [FromBody] CreateRcsInventoryInstanceRequest request, CancellationToken token = default)
    {
        var type = Normalize(request.ItemType);
        if (!IsItemType(type)) return BadRequest(RcsApiResponse.Rejected("类型必须是 CARGO 或 PALLET。"));
        var code = request.InstanceCode.Trim();
        var modelCode = request.ModelCode.Trim();
        if (code.Length == 0 || modelCode.Length == 0) return BadRequest(RcsApiResponse.Rejected("实例编码和模型编码不能为空。"));

        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Set<RcsInventoryModelRow>().AnyAsync(x => x.ItemType == type && x.ModelCode == modelCode && x.IsEnabled, token))
            return BadRequest(RcsApiResponse.Rejected("找不到已启用的对应类型模型。"));
        if (await db.Set<RcsInventoryInstanceRow>().AnyAsync(x => x.ItemType == type && x.InstanceCode == code, token))
            return Conflict(RcsApiResponse.Rejected("实例编码已存在。"));

        var parentCode = request.ParentInstanceCode.Trim();
        var mapCode = request.MapCode.Trim();
        var pointCode = request.PointCode.Trim();
        if (parentCode.Length > 0)
        {
            if (type != RcsInventoryItemTypes.Cargo || mapCode.Length > 0 || pointCode.Length > 0)
                return BadRequest(RcsApiResponse.Rejected("只有货物可以嵌套在托盘中；嵌套货物不能同时指定站点。"));
            var parent = await db.Set<RcsInventoryInstanceRow>().FirstOrDefaultAsync(
                x => x.ItemType == RcsInventoryItemTypes.Pallet && x.InstanceCode == parentCode, token);
            if (parent is null) return BadRequest(RcsApiResponse.Rejected("父实例不存在或不是托盘。"));
            if (string.IsNullOrWhiteSpace(parent.MapCode) || string.IsNullOrWhiteSpace(parent.PointCode))
                return Conflict(RcsApiResponse.Rejected("托盘当前不在储位上，不能向站点库存中添加嵌套货物。"));
            mapCode = parent.MapCode; pointCode = parent.PointCode;
        }
        else if (mapCode.Length == 0 || pointCode.Length == 0)
            return BadRequest(RcsApiResponse.Rejected("独立实例必须选择地图和站点。"));

        if (mapCode.Length > 0 && !await db.Set<RcsMapPointRow>().AnyAsync(x => x.MapCode == mapCode && x.PointCode == pointCode, token))
            return BadRequest(RcsApiResponse.Rejected("指定地图中不存在该站点。"));

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (parentCode.Length == 0 && await HasTopLevelInventoryAsync(db, mapCode, pointCode, token: token))
            return Conflict(RcsApiResponse.Rejected(OccupiedStationMessage(pointCode)));

        var now = DateTime.UtcNow;
        var row = new RcsInventoryInstanceRow
        {
            ItemType = type, InstanceCode = code, ModelCode = modelCode, MapCode = mapCode,
            PointCode = pointCode, ParentInstanceCode = parentCode, CreatedAt = now, UpdatedAt = now
        };
        db.Add(row);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Created($"/api/rcs/inventory/instances/{row.Id}", ToDto(row));
    }

    [HttpPut("instances/{id:long}/move")]
    public async Task<ActionResult<RcsInventoryInstanceDto>> MoveInstance(long id,
        [FromBody] MoveRcsInventoryInstanceRequest request, CancellationToken token = default)
    {
        var mapCode = request.MapCode.Trim();
        var pointCode = request.PointCode.Trim();
        if (mapCode.Length == 0 || pointCode.Length == 0) return BadRequest(RcsApiResponse.Rejected("目标地图和站点不能为空。"));
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.Set<RcsInventoryInstanceRow>().FirstOrDefaultAsync(x => x.Id == id, token);
        if (row is null) return NotFound(RcsApiResponse.Rejected("库存实例不存在。"));
        if (!await db.Set<RcsMapPointRow>().AnyAsync(x => x.MapCode == mapCode && x.PointCode == pointCode, token))
            return BadRequest(RcsApiResponse.Rejected("目标地图中不存在该站点。"));

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (await HasTopLevelInventoryAsync(db, mapCode, pointCode, id, token))
            return Conflict(RcsApiResponse.Rejected(OccupiedStationMessage(pointCode)));

        if (row.ItemType == RcsInventoryItemTypes.Pallet)
        {
            var nested = await db.Set<RcsInventoryInstanceRow>()
                .Where(x => x.ParentInstanceCode == row.InstanceCode).ToListAsync(token);
            foreach (var child in nested) { child.MapCode = mapCode; child.PointCode = pointCode; child.UpdatedAt = DateTime.UtcNow; }
        }
        row.MapCode = mapCode; row.PointCode = pointCode; row.ParentInstanceCode = ""; row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Ok(ToDto(row));
    }

    [HttpDelete("instances/{id:long}")]
    public async Task<IActionResult> DeleteInstance(long id, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.Set<RcsInventoryInstanceRow>().FirstOrDefaultAsync(x => x.Id == id, token);
        if (row is null) return NotFound(RcsApiResponse.Rejected("库存实例不存在。"));
        if (row.ItemType == RcsInventoryItemTypes.Pallet && await db.Set<RcsInventoryInstanceRow>().AnyAsync(x => x.ParentInstanceCode == row.InstanceCode, token))
            return Conflict(RcsApiResponse.Rejected("托盘中还有货物，请先移出或删除货物。"));
        db.Remove(row);
        await db.SaveChangesAsync(token);
        return Ok(RcsApiResponse.Accepted());
    }

    private static string? ValidateModel(RcsInventoryModelDto dto)
    {
        if (!IsItemType(dto.ItemType)) return "类型必须是 CARGO 或 PALLET。";
        if (string.IsNullOrWhiteSpace(dto.ModelCode) || string.IsNullOrWhiteSpace(dto.Name)) return "模型编码和名称不能为空。";
        if (dto.LengthMm < 0 || dto.WidthMm < 0 || dto.HeightMm < 0 || dto.WeightKg < 0 || dto.MaxLoadKg < 0)
            return "尺寸、重量和载荷不能小于零。";
        try { using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(dto.MetadataJson) ? "{}" : dto.MetadataJson); }
        catch (JsonException) { return "扩展信息必须是有效 JSON。"; }
        return null;
    }

    private static bool IsItemType(string? value) => value is not null
        && (Normalize(value) == RcsInventoryItemTypes.Cargo || Normalize(value) == RcsInventoryItemTypes.Pallet);
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string TypeName(string value) => value == RcsInventoryItemTypes.Pallet ? "托盘" : "货物";
    private static string OccupiedStationMessage(string pointCode) =>
        $"站点 {pointCode} 已有独立库存。每个站点只能放一个托盘或一个散放货物；如需放货，请将货物装入该站点的托盘。";
    private static async Task<bool> HasTopLevelInventoryAsync(
        GrcsDbContext db, string mapCode, string pointCode, long? excludeId = null, CancellationToken token = default)
    {
        var query = db.Set<RcsInventoryInstanceRow>().Where(x =>
            x.MapCode == mapCode && x.PointCode == pointCode && x.ParentInstanceCode == "");
        if (excludeId.HasValue) query = query.Where(x => x.Id != excludeId.Value);
        return await query.AnyAsync(token);
    }
    private static string NormalizeJson(string? value) => string.IsNullOrWhiteSpace(value) ? "{}" : value;
    private static RcsInventoryModelDto ToDto(RcsInventoryModelRow x) => new()
    {
        Id = x.Id, ItemType = x.ItemType, ModelCode = x.ModelCode, Name = x.Name,
        LengthMm = x.LengthMm, WidthMm = x.WidthMm, HeightMm = x.HeightMm, WeightKg = x.WeightKg,
        MaxLoadKg = x.MaxLoadKg, IsEnabled = x.IsEnabled, MetadataJson = x.MetadataJson
    };
    private static RcsInventoryInstanceDto ToDto(RcsInventoryInstanceRow x) => new()
    {
        Id = x.Id, ItemType = x.ItemType, InstanceCode = x.InstanceCode, ModelCode = x.ModelCode,
        Status = x.Status, MapCode = x.MapCode, PointCode = x.PointCode, ParentInstanceCode = x.ParentInstanceCode,
        OffsetXmm = x.OffsetXmm, OffsetYmm = x.OffsetYmm, OffsetZmm = x.OffsetZmm,
        MetadataJson = x.MetadataJson
    };
}
