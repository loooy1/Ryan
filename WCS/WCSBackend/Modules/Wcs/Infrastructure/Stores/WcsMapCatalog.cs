using System.Text.Json;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;

namespace WCSBackend.Modules.Wcs.Infrastructure;

/// <summary>从 wcs_slots 读取地图站点投影；地图几何与库存共用同一份持久化数据。</summary>
public sealed class WcsMapCatalog(IUnitOfWorkFactory uowFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public List<MapStationLite> GetStations()
    {
        using var uow = uowFactory.Create();
        return uow.Repository<WcsSlotRow>().Query().OrderBy(row => row.Mark)
            .ToList().Select(ToMapStation).ToList();
    }

    public MapUploadDto Snapshot()
    {
        using var uow = uowFactory.Create();
        var rows = uow.Repository<WcsSlotRow>().Query().OrderBy(row => row.Mark).ToList();
        return new MapUploadDto
        {
            SavedAt = rows.Select(row => row.UpdatedAt).Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderDescending().FirstOrDefault() ?? "",
            PathsCount = 0,
            Stations = rows.Select(ToMapStation).ToList(),
        };
    }

    private static MapStationLite ToMapStation(WcsSlotRow row)
    {
        var type = row.StationTypeBits != 0 ? row.StationTypeBits : row.SiteType switch
        {
            "Storage" => MapStationTypeBits.StorageLocation,
            "Terminal" => MapStationTypeBits.TransferPoint,
            "PeopleStation" => MapStationTypeBits.PeopleStation,
            "Sorting" => MapStationTypeBits.PickingStation,
            _ => 0,
        };

        List<string> cargoAreas;
        try { cargoAreas = JsonSerializer.Deserialize<List<string>>(row.CargoAreasJson, JsonOptions) ?? []; }
        catch (JsonException) { cargoAreas = []; }

        return new MapStationLite
        {
            Mark = row.Mark,
            StationType = type,
            X = row.X,
            Y = row.Y,
            Floor = row.Floor,
            StaEnable = row.StaEnable,
            CargoAreas = cargoAreas,
            SupportLoadState = row.SupportLoadState,
            AllowTurn = row.AllowTurn,
            AllowAvoid = row.AllowAvoid,
            AllowStop = row.AllowStop,
        };
    }
}
