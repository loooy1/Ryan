namespace Contracts.Rcs.Vehicle;

public sealed class RunVehicleRequest
{
    public string StartPointCode { get; init; } = "";
    public string EndPointCode { get; init; } = "";
}
