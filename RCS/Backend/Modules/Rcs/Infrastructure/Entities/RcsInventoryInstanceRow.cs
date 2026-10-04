namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

/// <summary>货物/托盘实体；ItemType 区分类型，ParentInstanceCode 表示货物装载在哪个托盘上。</summary>
public sealed class RcsInventoryInstanceRow
{
    public long Id { get; set; }
    public string ItemType { get; set; } = "CARGO";
    public string InstanceCode { get; set; } = "";
    public string ModelCode { get; set; } = "";
    public string Status { get; set; } = "AVAILABLE";
    public string MapCode { get; set; } = "";
    public string PointCode { get; set; } = "";
    public string ParentInstanceCode { get; set; } = "";
    public double OffsetXmm { get; set; }
    public double OffsetYmm { get; set; }
    public double OffsetZmm { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
