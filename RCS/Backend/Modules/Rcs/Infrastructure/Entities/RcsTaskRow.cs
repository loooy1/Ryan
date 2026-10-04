using System.Text.Json;
using System.Globalization;
using Contracts.Rcs.Tasks;

namespace RCSBackend.Modules.Rcs.Infrastructure.Entities;

public sealed class RcsTaskRow
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string MsgTime { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public int PriorityCode { get; set; }
    public string TaskType { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string StationCodesJson { get; set; } = "[]";
    public string StationActionsJson { get; set; } = "[]";
    public string AreaCodesJson { get; set; } = "[]";
    public string Status { get; set; } = RcsTaskStatus.Waiting;
    public string Source { get; set; } = RcsTaskSource.Upstream;
    public string VehicleId { get; set; } = "";
    public string RequestedVehicleId { get; set; } = "";
    public string MapCode { get; set; } = "";
    public int MapVersion { get; set; }
    public string RouteJson { get; set; } = "[]";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public RcsTaskDto ToDto() => new()
    {
        TaskId = TaskId, GroupId = GroupId, MsgTime = MsgTime, Warehouse = Warehouse,
        PriorityCode = PriorityCode, TaskType = TaskType, ContainerCode = ContainerCode,
        StationCode = JsonSerializer.Deserialize<string[]>(StationCodesJson) ?? [],
        StationActions = JsonSerializer.Deserialize<string[]>(StationActionsJson) ?? [],
        AreaCode = JsonSerializer.Deserialize<string[]>(AreaCodesJson) ?? [],
        Status = Status, Source = Source, VehicleId = VehicleId, RequestedVehicleId = RequestedVehicleId, MapCode = MapCode, MapVersion = MapVersion,
        RoutePointCodes = JsonSerializer.Deserialize<string[]>(RouteJson) ?? [], Message = Message,
        CreatedAt = LocalTime(CreatedAt),
        StartedAt = StartedAt.HasValue ? LocalTime(StartedAt.Value) : null,
        FinishedAt = FinishedAt.HasValue ? LocalTime(FinishedAt.Value) : null
    };

    private static string LocalTime(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc)
        .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
