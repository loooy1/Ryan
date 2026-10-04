using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Map;

namespace Rcs.Algorithms;

/// <summary>Produces the complete route and the route segments used by the execution layer.</summary>
public interface IPathPlanningAlgorithm
{
    AlgorithmRoutePlanDto Plan(
        RcsMapSnapshot map,
        AlgorithmPlanningContextDto fleet,
        string currentPointCode,
        IReadOnlyList<AlgorithmRouteStopDto> stops,
        AlgorithmSettingsDto settings,
        bool retainAnchor = false);
}
