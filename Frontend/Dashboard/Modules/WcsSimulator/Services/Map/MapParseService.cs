using System.Text.Json;
using Contracts.Dtos;
using Dashboard.Modules.WcsSimulator.Models;

namespace Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// 地图解析服务：map.json 原始数据（MapFileData）→ 精简站点列表（MapStationLite）。
/// 只做纯数据转换，页面仅负责状态与缓存写入。
/// </summary>
public static class MapParseService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>解析 map.json：全量 MapFileData 精简为 MapStationLite（只保留下发/筛选/表格展示需要的字段），按编码排序。</summary>
    public static (List<MapStationLite> Stations, int PathsCount) Parse(string content)
    {
        var map = JsonSerializer.Deserialize<MapFileData>(content, JsonOpts);
        var stations = map?.Stations?.Values
            .Select(s => new MapStationLite
            {
                Mark = s.Mark ?? "",
                StationType = s.StationType,
                X = s.X,
                Y = s.Y,
                Floor = s.Floor,
                StaEnable = s.StaEnable,
                CargoAreas = s.CargoAreas ?? [],
                SupportLoadState = s.SupportLoadState,
                AllowTurn = s.AllowTurn,
                AllowAvoid = s.AllowAvoid,
                AllowStop = s.AllowStop,
            })
            .OrderBy(s => s.Mark).ToList() ?? [];
        return (stations, map?.Paths?.Count ?? 0);
    }
}