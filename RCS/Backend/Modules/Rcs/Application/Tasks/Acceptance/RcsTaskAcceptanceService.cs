using System.Text.Json;
using Contracts.Rcs.Protocol;
using Contracts.Rcs.Tasks;
using RCSBackend.Modules.Rcs.Application.Execution;
using RCSBackend.Modules.Rcs.Application.Inventory;
using RCSBackend.Modules.Rcs.Application.Maps;
using RCSBackend.Modules.Rcs.Application.Vehicles;
using RCSBackend.Modules.Rcs.Infrastructure.Entities;
using RCSBackend.Modules.Rcs.Infrastructure.Stores;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

/// <summary>接收任务时校验地图、车辆与库存，完成预约和持久化；调用方负责串行化和启动执行。</summary>
public sealed class RcsTaskAcceptanceService(IRcsTaskStore store, RcsMapCache maps,
    IRcsTaskExecutionService executor, RcsVehicleRegistry vehicles, RcsInventoryTransferService inventory)
{
    public async Task ValidateAndInitializeAsync(RcsTaskReceiveRequest request, string source, CancellationToken token)
    {
        RcsTaskRequestValidator.Validate(request, allowManualTaskType: source == RcsTaskSource.Manual);
        await vehicles.InitializeAsync(token);
    }

    /// <summary>必须在共享调度锁内调用，避免手动车辆被并发任务同时领取。</summary>
    public async Task<IReadOnlyList<RcsTaskRow>> AcceptAsync(RcsTaskReceiveRequest request, string source,
        string originalRequestJson, Func<string, bool> isVehicleBusy,
        Action<RcsTaskRow, string> publish, CancellationToken token)
    {
        var existing = await store.FindAsync(request.Tasks.Select(x => x.TaskId).ToArray(), token);
        var byId = existing.ToDictionary(x => x.TaskId, StringComparer.OrdinalIgnoreCase);
        var added = new List<RcsTaskRow>();
        var reservedTaskIds = new List<string>();
        var currentMap = maps.Current;
        try
        {
            foreach (var input in request.Tasks)
            {
                if (byId.TryGetValue(input.TaskId, out var duplicate))
                {
                    if (!SameRequest(duplicate, request, input, source))
                        throw new RcsTaskConflictException($"TaskId {input.TaskId} 已存在且报文内容不同。");
                    if (source == RcsTaskSource.Upstream
                        && string.IsNullOrWhiteSpace(duplicate.OriginalRequestJson)
                        && !string.IsNullOrWhiteSpace(originalRequestJson))
                    {
                        duplicate.OriginalRequestJson = originalRequestJson;
                        duplicate.UpdatedAt = DateTime.UtcNow;
                        await store.UpdateAsync(duplicate, token);
                        publish(duplicate, "已保存上游系统原始报文");
                    }
                    continue;
                }
                var map = currentMap ?? throw new ArgumentException("没有当前地图，请先发布并加载地图。");
                if (!string.Equals(map.SceneName, request.Warehouse, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"场景 {request.Warehouse} 与当前地图场景 {map.SceneName} 不一致。");

                var normalizedStations = new List<string>(input.StationCode.Count);
                foreach (var code in input.StationCode)
                {
                    if (!map.TryGetPoint(code, out var point))
                        throw new ArgumentException($"任务 {input.TaskId} 的站点 {code} 不存在、已禁用或楼层与地图 Z 不匹配。");
                    normalizedStations.Add(point.PointCode);
                }
                RcsTaskRouteValidator.Validate(map, input.TaskId, normalizedStations);
                var stops = RcsTaskRoutePlanner.CreateStops(new(input.TaskId, input.VehicleId, map.MapCode,
                    request.Warehouse, normalizedStations, input.TaskType, input.ContainerCode, input.StationActions));
                var fetchStops = stops.Where(x => x.Action == VehiclePointAction.Fetch).ToArray();
                var putStops = stops.Where(x => x.Action == VehiclePointAction.Put).ToArray();
                if (fetchStops.Length > 0 || putStops.Length > 0)
                {
                    var stopArray = stops.ToArray();
                    if (fetchStops.Length != 1 || putStops.Length != 1
                        || Array.IndexOf(stopArray, fetchStops[0]) >= Array.IndexOf(stopArray, putStops[0]))
                        throw new ArgumentException("库存搬运任务必须先执行一次 fetch，再执行一次 put。");
                    try
                    {
                        await inventory.ReserveBeforeAcceptanceAsync(input.TaskId, map.MapCode,
                            input.ContainerCode, fetchStops[0].PointCode, putStops[0].PointCode, token);
                        reservedTaskIds.Add(input.TaskId);
                    }
                    catch (RcsInventoryPreconditionException ex) when (ex.IsConflict)
                    {
                        throw new RcsTaskConflictException(ex.Message);
                    }
                    catch (RcsInventoryPreconditionException ex)
                    {
                        throw new ArgumentException(ex.Message);
                    }
                }

                if (input.VehicleId != "")
                {
                    var target = executor.GetVehicles().FirstOrDefault(x => SameId(x.Id, input.VehicleId))
                        ?? throw new ArgumentException($"车辆 {input.VehicleId} 不存在。");
                    if (source == RcsTaskSource.Manual)
                    {
                        if (!target.IsEnabled) throw new ArgumentException($"车辆 {input.VehicleId} 已停用。");
                        if (!target.IsOnline) throw new RcsTaskConflictException($"车辆 {input.VehicleId} 当前离线，不能下发手动任务。");
                        if (target.OperatingMode != RcsOperatingMode.Manual)
                            throw new ArgumentException($"手动界面任务必须指定手动模式车辆，车辆 {input.VehicleId} 当前为自动模式。");
                    }
                }
                else if (source == RcsTaskSource.Manual)
                    throw new ArgumentException("手动界面任务必须指定一台手动模式车辆。");

                var row = new RcsTaskRow
                {
                    TaskId = input.TaskId, GroupId = request.GroupId, MsgTime = request.MsgTime,
                    Warehouse = request.Warehouse, PriorityCode = request.PriorityCode,
                    TaskType = input.TaskType, ContainerCode = input.ContainerCode, RequestedVehicleId = input.VehicleId,
                    Source = source,
                    OriginalRequestJson = source == RcsTaskSource.Upstream ? originalRequestJson : "",
                    StationCodesJson = JsonSerializer.Serialize(input.StationCode),
                    StationActionsJson = JsonSerializer.Serialize(input.StationActions),
                    ExecutionStagesJson = JsonSerializer.Serialize(RcsTaskExecutionStageFactory.CreatePending(input, map.MapCode, request.Warehouse)),
                    AreaCodesJson = JsonSerializer.Serialize(input.AreaCode), MapCode = map.MapCode,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                };
                if (source == RcsTaskSource.Manual)
                {
                    var target = executor.GetVehicles().First(x => SameId(x.Id, input.VehicleId));
                    if (isVehicleBusy(target.Id) || target.Status is not ("Idle" or "Arrived"))
                        throw new RcsTaskConflictException($"手动车辆 {target.Id} 当前忙碌，任务未下发。请等车辆空闲后重试。");
                    if (added.Any(x => SameId(x.VehicleId, target.Id)))
                        throw new ArgumentException($"同一批手动任务不能重复下发给车辆 {target.Id}。");
                    row.Status = RcsTaskStatus.Running;
                    row.VehicleId = target.Id;
                    row.StartedAt = DateTime.UtcNow;
                }
                added.Add(row); byId.Add(row.TaskId, row);
            }

            if (added.Count > 0) await store.AddAsync(added, token);
        }
        catch
        {
            foreach (var taskId in reservedTaskIds) inventory.Release(taskId);
            throw;
        }
        return added;
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SameRequest(RcsTaskRow row, RcsTaskReceiveRequest group, RcsTaskRequest task, string source) =>
        row.Source == source && SameId(row.GroupId, group.GroupId) && SameId(row.Warehouse, group.Warehouse)
        && row.PriorityCode == group.PriorityCode && SameId(row.TaskType, task.TaskType)
        && SameId(row.RequestedVehicleId, task.VehicleId) && row.ContainerCode == task.ContainerCode
        && row.StationCodesJson == JsonSerializer.Serialize(task.StationCode)
        && row.StationActionsJson == JsonSerializer.Serialize(task.StationActions)
        && row.AreaCodesJson == JsonSerializer.Serialize(task.AreaCode);
}
