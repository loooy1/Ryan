namespace Rcs.Contracts.Map;

public sealed class RouteDto
{
    public bool Found { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<GridPoint> Points { get; init; } = Array.Empty<GridPoint>();
}
