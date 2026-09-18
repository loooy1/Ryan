using Rcs.Contracts.Map;

namespace Rcs.Algorithms.AStar;

public interface IAStarPathfinder
{
    RouteDto FindPath(GridMapDto map, GridPoint start, GridPoint end);
}
