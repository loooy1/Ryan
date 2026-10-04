using Contracts.Rcs.Route;
using Contracts.Rcs.Map;

namespace Rcs.Algorithms.AStar;

public interface IAStarPathfinder
{
    RouteDto FindPath(RcsMapSnapshot map, string startPointCode, string endPointCode);
    RouteDto FindPath(RcsMapSnapshot map, string startPointCode, string endPointCode,
        IReadOnlySet<string> blockedPoints, IReadOnlySet<string> blockedLines);
}
