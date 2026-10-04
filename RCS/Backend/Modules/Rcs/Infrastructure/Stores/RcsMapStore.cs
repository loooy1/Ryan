using Backend.Shared.Infrastructure.Repository;
using Contracts.Rcs.Map;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;

namespace RCSBackend.Modules.Rcs.Infrastructure.Stores;

public sealed class RcsMapStore
{
    private readonly IUnitOfWorkFactory _uowFactory;

    public RcsMapStore(IUnitOfWorkFactory uowFactory) => _uowFactory = uowFactory;

    public Task<RcsMapEditorDto?> GetAsync(string mapCode, CancellationToken cancellationToken = default)
    {
        using var uow = _uowFactory.Create();
        var map = uow.Repository<RcsMapRow>().Query()
            .FirstOrDefault(x => x.MapCode == mapCode);
        if (map == null) return Task.FromResult<RcsMapEditorDto?>(null);

        var points = uow.Repository<RcsMapPointRow>().Query()
            .Where(x => x.MapCode == mapCode).ToList();
        var lines = uow.Repository<RcsMapLineRow>().Query()
            .Where(x => x.MapCode == mapCode).ToList();
        return Task.FromResult<RcsMapEditorDto?>(ToDto(map, points, lines));
    }

    public Task<RcsMapSnapshot?> LoadActiveSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var uow = _uowFactory.Create();
        var map = uow.Repository<RcsMapRow>().Query()
            .Where(x => x.IsActive && x.Status == "PUBLISHED")
            .OrderByDescending(x => x.UpdatedAt).FirstOrDefault()
            ?? uow.Repository<RcsMapRow>().Query().Where(x => x.Status == "PUBLISHED")
                .OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        if (map == null) return Task.FromResult<RcsMapSnapshot?>(null);
        var pointRows = uow.Repository<RcsMapPointRow>().Query().Where(x => x.MapCode == map.MapCode && x.IsEnabled).ToList();
        var points = pointRows.ToDictionary(x => x.PointCode,
            x => new RcsMapNode(x.PointCode, x.PointName, x.PointType, x.Floor, x.X, x.Y, x.Z),
            StringComparer.OrdinalIgnoreCase);
        var adjacency = points.Keys.ToDictionary(x => x, _ => new List<RcsMapEdge>(), StringComparer.OrdinalIgnoreCase);
        foreach (var line in uow.Repository<RcsMapLineRow>().Query().Where(x => x.MapCode == map.MapCode && x.IsEnabled).ToList())
        {
            if (!points.ContainsKey(line.FromPointCode) || !points.ContainsKey(line.ToPointCode)) continue;
            var edge = new RcsMapEdge(line.LineCode, line.FromPointCode, line.ToPointCode, line.Distance, line.Direction, line.MaxSpeed);
            adjacency[line.FromPointCode].Add(edge);
            if (!string.Equals(line.Direction, "FORWARD", StringComparison.OrdinalIgnoreCase))
                adjacency[line.ToPointCode].Add(edge with { FromPointCode = line.ToPointCode, ToPointCode = line.FromPointCode });
        }
        var snapshot = new RcsMapSnapshot
        {
            MapCode = map.MapCode, Name = map.Name, SceneName = map.SceneName, Version = map.Version,
            Points = new Dictionary<string, RcsMapNode>(points, StringComparer.OrdinalIgnoreCase),
            Adjacency = adjacency.ToDictionary(x => x.Key, x => (IReadOnlyList<RcsMapEdge>)x.Value, StringComparer.OrdinalIgnoreCase)
        };
        return Task.FromResult<RcsMapSnapshot?>(snapshot);
    }

    public async Task SaveAsync(RcsMapEditorDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.MapCode))
            throw new ArgumentException("地图编码不能为空。", nameof(dto));
        if (dto.Points.Any(x => string.IsNullOrWhiteSpace(x.PointCode)))
            throw new ArgumentException("地图点编码不能为空。", nameof(dto));
        var pointCodes = dto.Points.Select(x => x.PointCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dto.Lines.Any(x => string.IsNullOrWhiteSpace(x.FromPointCode)
            || string.IsNullOrWhiteSpace(x.ToPointCode)
            || !pointCodes.Contains(x.FromPointCode)
            || !pointCodes.Contains(x.ToPointCode)))
            throw new ArgumentException("地图线的起点和终点必须引用当前地图中的点。", nameof(dto));

        using var uow = _uowFactory.Create();
        var maps = uow.Repository<RcsMapRow>();
        var points = uow.Repository<RcsMapPointRow>();
        var lines = uow.Repository<RcsMapLineRow>();
        var map = maps.Query().FirstOrDefault(x => x.MapCode == dto.MapCode);
        var now = DateTime.UtcNow;
        if (map == null)
        {
            map = new RcsMapRow { MapCode = dto.MapCode, CreatedAt = now };
            await maps.AddAsync(map);
        }

        map.Name = dto.Name;
        map.SceneName = dto.SceneName;
        map.SourceType = dto.SourceType;
        map.Version = dto.Version;
        map.Status = dto.Status;
        map.CoordinateSystem = dto.CoordinateSystem;
        map.OriginX = dto.OriginX;
        map.OriginY = dto.OriginY;
        map.OriginZ = dto.OriginZ;
        map.IsActive = dto.IsActive;
        map.Description = dto.Description;
        map.UpdatedAt = now;

        await lines.DeleteWhereAsync(x => x.MapCode == dto.MapCode);
        await points.DeleteWhereAsync(x => x.MapCode == dto.MapCode);
        foreach (var point in dto.Points)
            await points.AddAsync(new RcsMapPointRow
            {
                MapCode = dto.MapCode, PointCode = point.PointCode, PointName = point.PointName,
                PointType = point.PointType, Floor = point.Floor, X = point.X, Y = point.Y, Z = point.Z,
                IsEnabled = point.IsEnabled, MetadataJson = point.MetadataJson,
                CreatedAt = now, UpdatedAt = now
            });
        foreach (var line in dto.Lines)
            await lines.AddAsync(new RcsMapLineRow
            {
                MapCode = dto.MapCode, LineCode = line.LineCode, FromPointCode = line.FromPointCode,
                ToPointCode = line.ToPointCode, Distance = line.Distance, Direction = line.Direction,
                MaxSpeed = line.MaxSpeed, IsEnabled = line.IsEnabled, MetadataJson = line.MetadataJson,
                CreatedAt = now, UpdatedAt = now
            });
        await uow.CommitAsync();
    }

    private static RcsMapEditorDto ToDto(RcsMapRow map, List<RcsMapPointRow> points, List<RcsMapLineRow> lines) => new()
    {
        MapCode = map.MapCode, Name = map.Name, SceneName = map.SceneName, SourceType = map.SourceType,
        Version = map.Version, Status = map.Status, CoordinateSystem = map.CoordinateSystem,
        OriginX = map.OriginX, OriginY = map.OriginY, OriginZ = map.OriginZ, IsActive = map.IsActive,
        Description = map.Description,
        Points = points.Select(x => new RcsMapPointDto
        {
            PointCode = x.PointCode, PointName = x.PointName, PointType = x.PointType,
            Floor = x.Floor, X = x.X, Y = x.Y, Z = x.Z, IsEnabled = x.IsEnabled, MetadataJson = x.MetadataJson
        }).ToList(),
        Lines = lines.Select(x => new RcsMapLineDto
        {
            LineCode = x.LineCode, FromPointCode = x.FromPointCode, ToPointCode = x.ToPointCode,
            Distance = x.Distance, Direction = x.Direction, MaxSpeed = x.MaxSpeed,
            IsEnabled = x.IsEnabled, MetadataJson = x.MetadataJson
        }).ToList()
    };
}
