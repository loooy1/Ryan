using Contracts.Rcs.Tasks;
using RCSBackend.Modules.Rcs.Application.Execution;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

/// <summary>Builds the task-stage view from task input and execution plans.</summary>
public static class RcsTaskExecutionStageFactory
{
    public static IReadOnlyList<RcsTaskExecutionStageDto> CreateForPlan(RcsTaskExecutionPlan plan)
    {
        var start = plan.Stops.LastOrDefault() ?? plan.TaskStops.First();
        var end = plan.RemainingStageStops.LastOrDefault() ?? plan.TaskStops.Last();
        var current = plan.RoutePlan.TotalPath.FirstOrDefault()?.PointCode ?? start.PointCode;
        return Create(plan.VehicleId, current, start, end);
    }

    public static IReadOnlyList<RcsTaskExecutionStageDto> CreatePending(
        RcsTaskRequest request, string mapCode, string warehouse)
    {
        var stops = RcsTaskRoutePlanner.CreateStops(new(request.TaskId, request.VehicleId, mapCode, warehouse,
            request.StationCode, request.TaskType, request.ContainerCode, request.StationActions));
        return stops.Count == 0 ? [] : Create(request.VehicleId, "", stops[0], stops[^1]);
    }

    private static IReadOnlyList<RcsTaskExecutionStageDto> Create(
        string vehicleId, string currentPointCode, RcsTaskStop start, RcsTaskStop end) =>
    [
        new()
        {
            Sequence = 1,
            StageCode = "VEHICLE_TO_START",
            Name = "车辆从当前位置前往起点",
            Status = RcsTaskExecutionStageStatus.Waiting,
            FromPointCode = currentPointCode,
            ToPointCode = start.PointCode,
            VehicleId = vehicleId
        },
        new()
        {
            Sequence = 2,
            StageCode = "START_ACTION",
            Name = "车辆在起点执行起点动作",
            Status = RcsTaskExecutionStageStatus.Waiting,
            FromPointCode = start.PointCode,
            ToPointCode = start.PointCode,
            Action = start.Action,
            VehicleId = vehicleId
        },
        new()
        {
            Sequence = 3,
            StageCode = "VEHICLE_TO_END",
            Name = "车辆从起点前往终点",
            Status = RcsTaskExecutionStageStatus.Waiting,
            FromPointCode = start.PointCode,
            ToPointCode = end.PointCode,
            VehicleId = vehicleId
        },
        new()
        {
            Sequence = 4,
            StageCode = "END_ACTION",
            Name = "车辆在终点执行终点动作",
            Status = RcsTaskExecutionStageStatus.Waiting,
            FromPointCode = end.PointCode,
            ToPointCode = end.PointCode,
            Action = end.Action,
            VehicleId = vehicleId
        }
    ];
}
