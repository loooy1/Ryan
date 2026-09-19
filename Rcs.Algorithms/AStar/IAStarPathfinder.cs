using Contracts.Rcs.Map;
using Contracts.Rcs.Route;

namespace Rcs.Algorithms.AStar;

public interface IAStarPathfinder
{
    RouteDto FindPath(GridMapDto map, GridPoint start, GridPoint end);
}
