namespace Dashboard.Modules.WcsSimulator.Models;

/// <summary>从 WCS 库存站点表返回的地图投影。</summary>
public class MapSnapshotDto
{
    public string SavedAt { get; set; } = "";
    public int PathsCount { get; set; }
    public List<Contracts.Dtos.MapStationLite> Stations { get; set; } = [];
}
