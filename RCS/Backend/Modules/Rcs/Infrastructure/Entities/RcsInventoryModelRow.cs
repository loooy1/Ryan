namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

/// <summary>货物/托盘的可复用尺寸模型；ItemType 区分 CARGO 与 PALLET。</summary>
public sealed class RcsInventoryModelRow
{
    public long Id { get; set; }
    public string ItemType { get; set; } = "CARGO";
    public string ModelCode { get; set; } = "";
    public string Name { get; set; } = "";
    public double LengthMm { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public double WeightKg { get; set; }
    public double? MaxLoadKg { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
