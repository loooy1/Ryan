namespace Contracts.Rcs.Inventory;

public static class RcsInventoryItemTypes
{
    public const string Cargo = "CARGO";
    public const string Pallet = "PALLET";
}

public sealed class RcsInventoryModelDto
{
    public long Id { get; set; }
    public string ItemType { get; set; } = RcsInventoryItemTypes.Cargo;
    public string ModelCode { get; set; } = "";
    public string Name { get; set; } = "";
    public double LengthMm { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public double WeightKg { get; set; }
    public double? MaxLoadKg { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string MetadataJson { get; set; } = "{}";
}

public sealed class RcsInventoryInstanceDto
{
    public long Id { get; set; }
    public string ItemType { get; set; } = RcsInventoryItemTypes.Cargo;
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
}

public sealed class CreateRcsInventoryInstanceRequest
{
    public string ItemType { get; set; } = RcsInventoryItemTypes.Cargo;
    public string InstanceCode { get; set; } = "";
    public string ModelCode { get; set; } = "";
    public string MapCode { get; set; } = "";
    public string PointCode { get; set; } = "";
    public string ParentInstanceCode { get; set; } = "";
}

public sealed class MoveRcsInventoryInstanceRequest
{
    public string MapCode { get; set; } = "";
    public string PointCode { get; set; } = "";
}
