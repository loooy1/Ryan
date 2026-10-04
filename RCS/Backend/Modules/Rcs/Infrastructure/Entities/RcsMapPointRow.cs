namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

/// <summary>RCS 地图点表。MapCode 和 PointCode 的关联由应用层维护。</summary>
public sealed class RcsMapPointRow
{
    public long Id { get; set; }
    public string MapCode { get; set; } = "";
    public string PointCode { get; set; } = "";
    public string PointName { get; set; } = "";
    public string PointType { get; set; } = "";
    public int Floor { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string MetadataJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
