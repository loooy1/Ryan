using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class RangeConfigService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly MapStoreService _map;
    private readonly object _lock = new();
    private RangeConfigDto _range = new();

    public RangeConfigService(IUnitOfWorkFactory uowFactory, MapStoreService map)
    {
        _uow = uowFactory; _map = map;
        try
        {
            var json = KvAccess.Get(_uow, "auto_range");
            if (!string.IsNullOrEmpty(json))
                _range = new KvRow { Value = json }.Adapt<RangeConfigDto>();
        }
        catch { }
    }

    public RangeConfigDto Get()
    {
        lock (_lock)
        {
            var copy = JsonSerializer.Deserialize<RangeConfigDto>(JsonSerializer.Serialize(_range))!;
            copy.TypeFilter = 0;   // 站点类型过滤已废弃
            copy.Marks = RemovePickingStationMarks(copy.Marks);
            return copy;
        }
    }

    public void Set(RangeConfigDto range)
    {
        lock (_lock)
        {
            // 站点类型过滤已废弃：写入时强制清零（清理历史遗留值）
            range.TypeFilter = 0;
            range.Marks = RemovePickingStationMarks(range.Marks);
            _range = range;
            KvAccess.Set(_uow, "auto_range", _range.Adapt<KvRow>().Value);
        }
    }

    private List<string> RemovePickingStationMarks(IEnumerable<string>? marks)
    {
        var pickingMarks = _map.GetStations()
            .Where(station => (station.StationType & MapStationTypeBits.PickingStation) != 0)
            .Select(station => station.Mark)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (marks ?? [])
            .Where(mark => !string.IsNullOrWhiteSpace(mark))
            .Select(mark => mark.Trim())
            .Where(mark => !pickingMarks.Contains(mark))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>运行配置（GRCS 地址/场景名；前端「连接设置」PUT 到这里）。Singleton。</summary>
