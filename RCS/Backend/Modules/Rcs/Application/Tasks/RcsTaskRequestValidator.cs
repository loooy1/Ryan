using System.Globalization;
using Contracts.Rcs.Tasks;
using Contracts.Rcs.Protocol;

namespace RCSBackend.Modules.Rcs.Application.Tasks;

internal static class RcsTaskRequestValidator
{
    public static void Validate(RcsTaskReceiveRequest request, bool allowManualTaskType = false)
    {
        static void Required(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
                throw new ArgumentException($"{field} 不能为空且不能超过 128 字符。");
        }
        Required(request.GroupId, "GroupId"); Required(request.Warehouse, "Warehouse");
        if (!DateTime.TryParseExact(request.MsgTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _)) throw new ArgumentException("MsgTime 必须是 yyyy-MM-dd HH:mm:ss 格式。");
        if (request.PriorityCode < 0) throw new ArgumentException("PriorityCode 不能小于 0。");
        if (request.Tasks is null || request.Tasks.Count is < 1 or > 100)
            throw new ArgumentException("Tasks 必须包含 1～100 个任务。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in request.Tasks)
        {
            if (task is null) throw new ArgumentException("Tasks 不能包含 null。");
            Required(task.TaskId, "TaskId"); Required(task.TaskType, "TaskType");
            if (task.VehicleId is null || task.VehicleId.Length > 64)
                throw new ArgumentException("VehicleId 必须是字符串且最多 64 字符；为空时自动分配。");
            if (!ids.Add(task.TaskId)) throw new ArgumentException($"同一报文中 TaskId {task.TaskId} 重复。");
            if (task.ContainerCode is null || task.ContainerCode.Length > 128)
                throw new ArgumentException("ContainerCode 不能是 null 或超过 128 字符。");
            if (task.StationCode is null || task.StationCode.Count is < 1 or > 100)
                throw new ArgumentException("StationCode 必须包含 1～100 个站点。");
            foreach (var code in task.StationCode) Required(code, "StationCode");
            var taskType = task.TaskType.ToUpperInvariant();
            if (taskType is not ("MOVE_ONLY" or "INBOUND" or "OUTBOUND")
                && !(allowManualTaskType && taskType == "MANUAL"))
                throw new ArgumentException(allowManualTaskType
                    ? "TaskType 仅支持 MOVE_ONLY、INBOUND、OUTBOUND、MANUAL。"
                    : "TaskType 仅支持 MOVE_ONLY、INBOUND、OUTBOUND。");
            if (task.TaskType.ToUpperInvariant() is "INBOUND" or "OUTBOUND"
                && (task.StationCode.Count < 2 || string.IsNullOrWhiteSpace(task.ContainerCode)))
                throw new ArgumentException("INBOUND/OUTBOUND 至少需要两个站点及 ContainerCode。");
            if (task.StationActions is null || task.StationActions.Count > 100)
                throw new ArgumentException("StationActions 必须是数组，最多 100 项。");
            if (task.StationActions.Count > 0)
            {
                if (task.StationActions.Count != task.StationCode.Count)
                    throw new ArgumentException("StationActions 数量必须与 StationCode 一致。");
                for (var i = 0; i < task.StationActions.Count; i++)
                {
                    var action = task.StationActions[i]?.Trim().ToLowerInvariant();
                    if (action is not (VehiclePointAction.Move or VehiclePointAction.Fetch or VehiclePointAction.Put))
                        throw new ArgumentException("StationActions 仅支持 move、fetch、put。");
                    task.StationActions[i] = action;
                }
                if (task.StationActions.Any(action => action is VehiclePointAction.Fetch or VehiclePointAction.Put)
                    && string.IsNullOrWhiteSpace(task.ContainerCode))
                    throw new ArgumentException("包含 fetch 或 put 动作时必须填写 ContainerCode。");
            }
            if (taskType == "MANUAL" && task.StationActions.Count == 0)
                throw new ArgumentException("MANUAL 任务必须为每个站点指定动作。");
            if (task.AreaCode is null || task.AreaCode.Count > 100)
                throw new ArgumentException("AreaCode 必须是数组，最多 100 项。");
            foreach (var code in task.AreaCode) Required(code, "AreaCode");
        }
    }
}
