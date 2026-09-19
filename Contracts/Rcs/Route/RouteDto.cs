using Contracts.Rcs.Map;

namespace Contracts.Rcs.Route;

public sealed class RouteDto
{
    public bool Found { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<GridPoint> Points { get; init; } = Array.Empty<GridPoint>();
}
