namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

/// <summary>RCS 地图线表。起点和终点使用点编码，不使用数据库外键。</summary>
public sealed class RcsMapLineRow
{
    public long Id { get; set; }
    public string MapCode { get; set; } = "";
    public string LineCode { get; set; } = "";
    public string FromPointCode { get; set; } = "";
    public string ToPointCode { get; set; } = "";
    public double Distance { get; set; }
    public string Direction { get; set; } = "";
    public double MaxSpeed { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string MetadataJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
