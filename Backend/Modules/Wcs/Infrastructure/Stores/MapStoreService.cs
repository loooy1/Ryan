using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class MapStoreService
{
    private readonly IUnitOfWorkFactory _uow;
    private readonly object _lock = new();
    private List<MapStationLite> _stations = [];
    private string _savedAt = "";
    private int _pathsCount;

    public MapStoreService(IUnitOfWorkFactory uowFactory)
    {
        _uow = uowFactory;
        try
        {
            var json = KvAccess.Get(_uow, "map_stations");
            if (!string.IsNullOrEmpty(json))
            {
                var dto = new KvRow { Value = json }.Adapt<MapUploadDto>();
                _stations = dto.Stations;
                _savedAt = dto.SavedAt;
                _pathsCount = dto.PathsCount;
            }
        }
        catch { }
    }

    public void Save(MapUploadDto dto)
    {
        lock (_lock)
        {
            _stations = dto.Stations ?? [];
            _savedAt = dto.SavedAt;
            _pathsCount = dto.PathsCount;
            KvAccess.Set(_uow, "map_stations", dto.Adapt<KvRow>().Value);
        }
    }

    public List<MapStationLite> GetStations() { lock (_lock) { return _stations.ToList(); } }

    public object Snapshot() { lock (_lock) { return new { savedAt = _savedAt, pathsCount = _pathsCount, stations = _stations }; } }
}

/// <summary>选点范围配置（内存 + SQLite 持久化）。Singleton。</summary>
