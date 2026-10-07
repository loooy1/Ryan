using Contracts.Rcs.Algorithm;
using Contracts.Rcs.Map;
using Contracts.Rcs.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rcs.Algorithms;
using Rcs.Algorithms.AStar;
using Rcs.Algorithms.Traffic;
using RCSBackend.Modules.Rcs.Application.Execution;

namespace Rcs.Architecture.Tests;

[TestClass]
public sealed class RouteAndTrafficTests
{
    [TestMethod]
    public void AlgorithmPathIsConvertedToVehicleMoveWindows()
    {
        var planner = new RcsTaskRoutePlanner(new AStarRoutePlanningAlgorithm(new AStarPathfinder()));
        var fleet = new AlgorithmPlanningContextDto("V-01", [], [], []);
        var route = planner.Plan(Map(), fleet, "A", [new RcsTaskStop("task:0", "D", VehiclePointAction.Fetch)],
            new AlgorithmSettingsDto { SegmentPointCount = 3, AdvanceAfterPoints = 1 });

        CollectionAssert.AreEqual(new[] { "A", "B", "C", "D" }, route.TotalPath.Select(x => x.PointCode).ToArray());
        Assert.IsTrue(route.TotalPath.All(x => x.Action == VehiclePointAction.Move && x.StepId == ""));
        Assert.AreEqual(2, route.Segments.Count);
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, route.Segments[0].Points.Select(x => x.PointCode).ToArray());
        CollectionAssert.AreEqual(new[] { "B", "C", "D" }, route.Segments[1].Points.Select(x => x.PointCode).ToArray());
    }

    [TestMethod]
    public void ConfirmedProgressReleasesPassedPointAndLineLocks()
    {
        var traffic = new MultiVehicleTrafficCoordinator();
        var map = Map();
        Assert.IsTrue(traffic.TryAcquireRouteWindow(map, "V-01", "task-1", ["A", "B", "C"], 0, 1,
            out var first, out _));
        var firstLease = first ?? throw new AssertFailedException("首个路径窗口未获得锁。");
        using (firstLease) firstLease.Commit();
        Assert.IsFalse(traffic.TryAcquirePosition("V-02", "task-2", "A", out _, out _));

        traffic.ObserveProgress("V-01", "task-1", 1, 2, "B");

        CollectionAssert.AreEqual(new[] { "B", "C" }, traffic.GetLockedPoints("V-01").ToArray());
        CollectionAssert.AreEqual(new[] { "BC" }, traffic.GetLockedLines("V-01").ToArray());
        Assert.IsTrue(traffic.TryAcquirePosition("V-02", "task-2", "A", out var second, out _));
        var secondLease = second ?? throw new AssertFailedException("已通过点仍未释放。");
        using (secondLease) secondLease.Commit();
        CollectionAssert.AreEqual(new[] { "A" }, traffic.GetLockedPoints("V-02").ToArray());
    }

    private static RcsMapSnapshot Map()
    {
        var points = new Dictionary<string, RcsMapNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<string, IReadOnlyList<RcsMapEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, x) in new[] { ("A", 0d), ("B", 1d), ("C", 2d), ("D", 3d) })
            points.Add(code, new RcsMapNode(code, "WAYPOINT", x, 0, 1));
        foreach (var code in points.Keys) edges.Add(code, []);
        foreach (var (from, to) in new[] { ("A", "B"), ("B", "C"), ("C", "D") })
        {
            var line = from + to;
            edges[from] = edges[from].Append(new RcsMapEdge(line, from, to, 1, "BIDIRECTIONAL", 1)).ToArray();
            edges[to] = edges[to].Append(new RcsMapEdge(line, to, from, 1, "BIDIRECTIONAL", 1)).ToArray();
        }
        return new RcsMapSnapshot { MapCode = "test", SceneName = "AMR", Points = points, Adjacency = edges };
    }
}
