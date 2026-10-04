namespace Contracts.Rcs.Algorithm;

/// <summary>车辆遥测投影；不包含任务业务字段或点位动作。</summary>
public sealed record AlgorithmVehiclePositionDto(
    string VehicleId,
    string PointCode,
    bool IsMoving,
    DateTimeOffset ObservedAt);

/// <summary>算法观察到的车队状态与目标，仅供多车路线规划使用。</summary>
public sealed record AlgorithmVehiclePlanningStateDto(
    string VehicleId,
    string CurrentPointCode,
    IReadOnlyList<string> GoalPointCodes,
    bool IsMoving);

/// <summary>单车路线请求所需的多车上下文；预约资源由交通控制层统一授予。</summary>
public sealed record AlgorithmPlanningContextDto(
    string VehicleId,
    IReadOnlyList<AlgorithmVehiclePlanningStateDto> Vehicles,
    IReadOnlyList<string> ReservedPointCodes,
    IReadOnlyList<string> ReservedLineCodes);
