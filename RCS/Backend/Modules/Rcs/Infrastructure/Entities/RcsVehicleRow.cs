namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

public sealed class RcsVehicleRow
{
    public string VehicleId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "VIRTUAL";
    public string OperatingMode { get; set; } = "AUTO";
    public string InitialPointCode { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
