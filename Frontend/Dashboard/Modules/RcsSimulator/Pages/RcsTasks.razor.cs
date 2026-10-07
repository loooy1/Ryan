using System.Globalization;
using System.Text;
using System.Text.Json;
using Contracts.Rcs.Inventory;
using Contracts.Rcs.Map;
using Contracts.Rcs.Route;
using Contracts.Rcs.Tasks;
using Contracts.Rcs.Vehicle;
using Dashboard.Modules.RcsSimulator.Components;
using Dashboard.Modules.RcsSimulator.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Dashboard.Modules.RcsSimulator.Pages;

public partial class RcsTasks
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, RcsTaskDto> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VehicleStateDto> _vehicles = new(StringComparer.OrdinalIgnoreCase);
    private RcsTaskPageDto _pageInfo = new();
    private CancellationTokenSource? _searchDelay;
    private CancellationTokenSource? _taskRefreshDelay;
    private string _search = "", _statusFilter = "", _error = "", _notice = "";
    private bool _loading, _disposed, _refreshPending, _confirmClear, _clearing;
    private int _page = 1, _pageSize = 10, _jumpPage = 1;

    protected override async Task OnInitializedAsync()
    {
        try { var saved = await Js.InvokeAsync<string?>("localStorage.getItem", "rcs_backend_url"); if (!string.IsNullOrWhiteSpace(saved)) Api.SetBaseUrl(saved); } catch { }
        Realtime.TaskChanged += OnTaskChanged; Realtime.VehicleStateChanged += OnVehicleStateChanged;
        Realtime.VehiclesChanged += OnVehiclesChanged;
        Realtime.TasksCleared += OnTasksCleared;
        Realtime.Reconnected += OnReconnected;
        await RefreshAsync();
        if (_disposed) return;
        Realtime.SetBaseUrl(Api.BaseUrl);
        try { await Realtime.StartAsync(); } catch { }
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        if (_loading) { _refreshPending = true; return; }
        _loading = true; _error = "";
        try
        {
            var tasksRequest = Api.GetTaskPageAsync(_page, _pageSize, _statusFilter, _search, _lifetime.Token);
            var vehiclesRequest = Api.GetVehiclesAsync(_lifetime.Token);
            var page = await tasksRequest;
            _pageInfo = page;
            _page = page.Page;
            _pageSize = page.PageSize;
            _jumpPage = _page;
            _tasks.Clear(); foreach (var task in page.Items) _tasks[task.TaskId] = task;
            await InvokeAsync(StateHasChanged);
            var vehicles = await vehiclesRequest;
            _vehicles.Clear(); foreach (var vehicle in vehicles) _vehicles[vehicle.Id] = vehicle;
            if (!Realtime.IsConnected) { Realtime.SetBaseUrl(Api.BaseUrl); try { await Realtime.StartAsync(); } catch { } }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) when (!_disposed) { _error = ex.Message; }
        finally
        {
            _loading = false;
            if (_refreshPending && !_disposed)
            {
                _refreshPending = false;
                _ = InvokeAsync(RefreshAsync);
            }
            else if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private Task RefreshAsyncFromHub() => InvokeAsync(RefreshAsync);
    private void OnReconnected() => _ = RefreshAsyncFromHub();
    private void OnTaskChanged(RcsTaskDto task) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        if (_tasks.ContainsKey(task.TaskId)) _tasks[task.TaskId] = task;
        QueueTaskRefresh();
        StateHasChanged();
    });
    private void OnTasksCleared(int deletedCount) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        _tasks.Clear(); _pageInfo = new(); _page = _jumpPage = 1;
        StateHasChanged();
    });
    private void OnVehicleStateChanged(VehicleStateDto vehicle) => _ = InvokeAsync(() => { if (!_disposed) { _vehicles[vehicle.Id] = vehicle; StateHasChanged(); } });
    private void OnVehiclesChanged(IReadOnlyList<VehicleStateDto> vehicles) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        _vehicles.Clear(); foreach (var vehicle in vehicles) _vehicles[vehicle.Id] = vehicle;
        StateHasChanged();
    });

    private void QueueTaskRefresh()
    {
        if (_loading) { _refreshPending = true; return; }
        _taskRefreshDelay?.Cancel();
        var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _taskRefreshDelay = delay;
        _ = RefreshAfterDelayAsync(delay);
    }

    private async Task RefreshAfterDelayAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(200, delay.Token);
            await InvokeAsync(RefreshAsync);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_taskRefreshDelay, delay)) _taskRefreshDelay = null;
            delay.Dispose();
        }
    }
    private async Task SearchChangedAsync(ChangeEventArgs args)
    {
        _search = args.Value?.ToString() ?? "";
        _searchDelay?.Cancel(); _searchDelay?.Dispose();
        _searchDelay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try { await Task.Delay(250, _searchDelay.Token); }
        catch (OperationCanceledException) { return; }
        _page = 1;
        await RefreshAsync();
    }

    private async Task StatusChangedAsync(ChangeEventArgs args)
    {
        _statusFilter = args.Value?.ToString() ?? "";
        _page = 1;
        await RefreshAsync();
    }

    private async Task PageSizeChangedAsync(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var value)) _pageSize = Math.Clamp(value, 10, 100);
        _page = 1;
        await RefreshAsync();
    }

    private async Task PreviousPageAsync() { if (_page > 1) { _page--; await RefreshAsync(); } }
    private async Task NextPageAsync() { if (_page < _pageInfo.TotalPages) { _page++; await RefreshAsync(); } }
    private async Task JumpPageAsync()
    {
        _page = Math.Clamp(_jumpPage, 1, Math.Max(1, _pageInfo.TotalPages));
        await RefreshAsync();
    }

    private void CloseClearDialog() { if (!_clearing) _confirmClear = false; }

    private async Task ClearAllTasksAsync()
    {
        if (_clearing) return;
        _clearing = true; _error = ""; _notice = "";
        try
        {
            var result = await Api.ClearAllTasksAsync(_lifetime.Token);
            _confirmClear = false;
            _search = ""; _statusFilter = ""; _page = _jumpPage = 1;
            _notice = $"已清空全部任务，共删除 {result.DeletedCount} 条记录。";
            await RefreshAsync();
        }
        catch (Exception ex) when (!_disposed) { _error = ex.Message; }
        finally { _clearing = false; if (!_disposed) await InvokeAsync(StateHasChanged); }
    }

    private static string StatusText(string status) => status switch
    {
        RcsTaskStatus.Waiting => "待分配", RcsTaskStatus.Running => "执行中", RcsTaskStatus.Paused => "已暂停",
        RcsTaskStatus.Cancelling => "取消中", RcsTaskStatus.Completed => "已完成", RcsTaskStatus.Failed => "失败",
        RcsTaskStatus.Cancelled => "已取消", RcsTaskStatus.Interrupted => "已中断", _ => status
    };
    private static string StatusClass(string status) => status switch
    {
        RcsTaskStatus.Completed => "status-done", RcsTaskStatus.Waiting => "status-waiting",
        RcsTaskStatus.Failed or RcsTaskStatus.Cancelled or RcsTaskStatus.Interrupted => "status-error",
        RcsTaskStatus.Paused or RcsTaskStatus.Cancelling => "status-paused", _ => "status-running"
    };

    private static string StageTimelineClass(string status) => status switch
    {
        RcsTaskExecutionStageStatus.Completed => "done",
        RcsTaskExecutionStageStatus.Running => "current",
        RcsTaskExecutionStageStatus.Failed or RcsTaskExecutionStageStatus.Cancelled => "failed",
        _ => "pending"
    };
    private static string ActionText(string action) => action switch { "fetch" => "取货", "put" => "放货", _ => "移动" };
    private static bool IsCarryTaskType(string taskType) =>
        string.Equals(taskType, "Auto_Carry", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskType, "INBOUND", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskType, "OUTBOUND", StringComparison.OrdinalIgnoreCase);
    private static string InternalPointCode(string stationCode)
    {
        var separator = stationCode.LastIndexOf('_');
        return separator > 0 && int.TryParse(stationCode.AsSpan(separator + 1), out _)
            ? stationCode[..separator] : stationCode;
    }
    private static string ActionCodeAt(RcsTaskDto task, int index, bool isEnd)
    {
        if (index < task.StationActions.Count && !string.IsNullOrWhiteSpace(task.StationActions[index]))
            return task.StationActions[index];
        if (IsCarryTaskType(task.TaskType))
            return isEnd ? "put" : "fetch";
        return "move";
    }

    private static string TaskParameterJson(RcsTaskDto task)
    {
        if (task.Source != RcsTaskSource.Manual)
            return string.IsNullOrWhiteSpace(task.OriginalRequestJson)
                ? "该任务创建于原始报文留存功能启用前，数据库中没有保存上游原始报文。"
                : task.OriginalRequestJson;

        var startIndex = 0;
        var endIndex = Math.Max(0, task.StationCode.Count - 1);
        return JsonSerializer.Serialize(new
        {
            GroupId = task.GroupId,
            TaskId = task.TaskId,
            MsgTime = task.MsgTime,
            PriorityCode = task.PriorityCode,
            Warehouse = task.Warehouse,
            VehicleId = string.IsNullOrWhiteSpace(task.RequestedVehicleId) ? task.VehicleId : task.RequestedVehicleId,
            TaskType = task.TaskType,
            ContainerCode = task.ContainerCode,
            StartStationCode = task.StationCode.FirstOrDefault() ?? "",
            StartAction = ActionCodeAt(task, startIndex, isEnd: false),
            EndStationCode = task.StationCode.LastOrDefault() ?? "",
            EndAction = ActionCodeAt(task, endIndex, isEnd: true)
        }, PrettyJson);
    }

    private static IReadOnlyList<TimelineStep> Timeline(RcsTaskDto task, VehicleStateDto? vehicle)
    {
        var stops = task.StationCode;
        var start = stops.FirstOrDefault() ?? "";
        var end = stops.LastOrDefault() ?? "";
        var firstDone = vehicle?.CompletedStepIds.Contains($"{task.TaskId}:0", StringComparer.Ordinal) == true;
        var lastDone = vehicle?.CompletedStepIds.Contains($"{task.TaskId}:{Math.Max(0, stops.Count - 1)}", StringComparer.Ordinal) == true;
        var atStart = vehicle?.TaskId == task.TaskId
            && string.Equals(vehicle.PointCode, InternalPointCode(start), StringComparison.OrdinalIgnoreCase);
        var atEnd = vehicle?.TaskId == task.TaskId
            && string.Equals(vehicle.PointCode, InternalPointCode(end), StringComparison.OrdinalIgnoreCase);
        var terminal = task.Status is RcsTaskStatus.Completed or RcsTaskStatus.Failed or RcsTaskStatus.Cancelled or RcsTaskStatus.Interrupted;
        var current = task.Status == RcsTaskStatus.Waiting ? 0
            : task.VehicleId == "" ? 1
            : task.Status == RcsTaskStatus.Completed ? 6
            : task.RoutePointCodes.Count == 0 ? 1
            : !firstDone ? (atStart ? 3 : 2)
            : !lastDone ? (atEnd ? 5 : 4)
            : 6;
        if (task.Status == RcsTaskStatus.Cancelled && task.VehicleId == "") current = 1;
        var isCarryTask = IsCarryTaskType(task.TaskType);
        var startAction = isCarryTask ? "取货动作" : "起点动作（move）";
        var endAction = isCarryTask ? "放货动作" : "终点动作（move）";
        var titles = new[] { "收到任务，等待分配", "分配给车辆", "车辆从当前位置前往起点", $"车辆在起点执行{startAction}", "车辆从起点前往终点", $"车辆在终点执行{endAction}", "任务完成" };
        var details = new[]
        {
            task.Status == RcsTaskStatus.Waiting ? "已接收，正在等待调度器分配。" : $"上游任务 {task.GroupId} 已被 RCS 接收。",
            task.VehicleId == "" ? "尚未分配车辆。" : $"已分配车辆 {task.VehicleId}。",
            start == "" ? "报文未提供起点站。" : vehicle is null ? $"目标起点 {start}，等待车辆状态。" : $"当前 {vehicle.PointCode} · 目标 {start}。",
            start == "" ? "无起点站动作。" : $"站点 {start} · {(isCarryTask ? "fetch" : "move")}。",
            start == "" || end == "" ? "报文未提供完整起终点。" : $"{start} → {end} · 路径 {task.RoutePointCodes.Count} 点。",
            end == "" ? "无终点站动作。" : $"站点 {end} · {(isCarryTask ? "put" : "move")}。",
            task.FinishedAt is null ? "等待车辆完成终点动作。" : $"完成时间 {task.FinishedAt}。"
        };
        var timeline = Enumerable.Range(0, 7).Select(index => new TimelineStep(index + 1, titles[index], details[index],
            task.Status == RcsTaskStatus.Completed || index < current ? "done"
            : index == current && task.Status == RcsTaskStatus.Failed ? "failed"
            : index == current && !terminal ? "current"
            : index == current && task.Status is RcsTaskStatus.Cancelled or RcsTaskStatus.Interrupted ? "failed" : "pending")).ToArray();

        foreach (var stage in task.ExecutionStages.Where(x => x.Sequence is >= 1 and <= 4))
        {
            var index = stage.Sequence + 1;
            var state = StageTimelineClass(stage.Status);
            var stageDetails = new List<string>();
            var route = string.IsNullOrWhiteSpace(stage.FromPointCode)
                ? stage.ToPointCode : $"{stage.FromPointCode} → {stage.ToPointCode}";
            if (route != "") stageDetails.Add(route);
            if (stage.Action != "") stageDetails.Add(ActionText(stage.Action));
            if (stage.VehicleId != "") stageDetails.Add($"车辆 {stage.VehicleId}");
            if (stage.CommandId != "") stageDetails.Add($"命令 {stage.CommandId}");
            if (stage.StartedAt is not null) stageDetails.Add($"开始 {stage.StartedAt}");
            if (stage.FinishedAt is not null) stageDetails.Add($"结束 {stage.FinishedAt}");
            if (state == "failed" && stage.Message != "") stageDetails.Add(stage.Message);

            var confirmed = stage.Status == RcsTaskExecutionStageStatus.Completed;
            timeline[index] = new TimelineStep(stage.Sequence + 2, stage.Name, string.Join(" · ", stageDetails), state,
                confirmed ? "车辆已确认完成" : "车辆未确认完成", confirmed ? "confirmed" : "unconfirmed");
        }

        return timeline;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
        _searchDelay?.Cancel(); _searchDelay?.Dispose();
        _taskRefreshDelay?.Cancel();
        Realtime.TaskChanged -= OnTaskChanged; Realtime.VehicleStateChanged -= OnVehicleStateChanged;
        Realtime.VehiclesChanged -= OnVehiclesChanged;
        Realtime.TasksCleared -= OnTasksCleared;
        Realtime.Reconnected -= OnReconnected;
        await Task.CompletedTask;
    }

    private sealed record TimelineStep(int Number, string Title, string Detail, string State,
        string Confirmation = "", string ConfirmationClass = "");
}
