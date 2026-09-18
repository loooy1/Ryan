namespace Rcs.Contracts.Map;

public sealed class GridMapDto
{
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyList<GridPoint> Obstacles { get; init; } = Array.Empty<GridPoint>();
    public GridPoint? Start { get; init; }
    public GridPoint? End { get; init; }
}
