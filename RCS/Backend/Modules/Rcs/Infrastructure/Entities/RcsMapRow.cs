namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

/// <summary>RCS 地图主表。点和线通过 MapCode 建立应用层关系，不使用数据库外键。</summary>
public sealed class RcsMapRow
{
    public long Id { get; set; }
    public string MapCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string SceneName { get; set; } = "";
    public string SourceType { get; set; } = "RCS";
    public int Version { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string CoordinateSystem { get; set; } = "WORLD";
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double OriginZ { get; set; }
    public bool IsActive { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
