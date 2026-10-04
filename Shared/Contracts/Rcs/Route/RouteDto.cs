namespace Contracts.Rcs.Route;

public sealed class RouteDto
{
    public bool Found { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> PointCodes { get; init; } = Array.Empty<string>();
}
