namespace Contracts.Rcs.Map;

/// <summary>RCS 编辑地图的完整传输模型。</summary>
public sealed class RcsMapEditorDto
{
    public string MapCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string SceneName { get; set; } = "";
    public string SourceType { get; set; } = "RCS";
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "DRAFT";
    public string CoordinateSystem { get; set; } = "WORLD";
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double OriginZ { get; set; }
    public bool IsActive { get; set; }
    public string Description { get; set; } = "";
    public List<RcsMapPointDto> Points { get; set; } = [];
    public List<RcsMapLineDto> Lines { get; set; } = [];
}

public sealed class RcsMapPointDto
{
    public string PointCode { get; set; } = "";
    public string PointName { get; set; } = "";
    public string PointType { get; set; } = "WAYPOINT";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string MetadataJson { get; set; } = "";
}

public sealed class RcsMapLineDto
{
    public string LineCode { get; set; } = "";
    public string FromPointCode { get; set; } = "";
    public string ToPointCode { get; set; } = "";
    public double Distance { get; set; }
    public string Direction { get; set; } = "BIDIRECTIONAL";
    public double MaxSpeed { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string MetadataJson { get; set; } = "";
}
