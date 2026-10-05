namespace Contracts.Rcs.Vehicle;

public sealed class CreateVehicleRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PointCode { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string OperatingMode { get; set; } = "AUTO";
    public string Protocol { get; set; } = "VIRTUAL";
}

public sealed class UpdateVehicleRequest
{
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public string OperatingMode { get; set; } = "";
    public string Protocol { get; set; } = "";
}

public sealed record VehicleProtocolInfoDto(string Code, string Name);
