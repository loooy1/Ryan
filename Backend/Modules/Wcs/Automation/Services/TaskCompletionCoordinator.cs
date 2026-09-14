using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 统一监管任务阶段副作用：LOAD_FINISH 推进库存，FINISHED 释放自动化任务资源，
/// 非自动化任务在 FINISHED 后执行终点模块。队列为进程内队列，当前不承担重启恢复。
/// </summary>
public sealed class TaskCompletionCoordinator : BackgroundService
{
    private readonly ITaskStageService _stage;
    private readonly TaskLifecycleService _lifecycle;
    private readonly WcsInventoryStore _inventory;
    private readonly ModuleRunService _modules;
    private readonly ILogger<TaskCompletionCoordinator> _logger;
    private readonly ConcurrentDictionary<string, Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _completionQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<WorkItem> _lifecycleChannel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    public TaskCompletionCoordinator(
        ITaskStageService stage,
        TaskLifecycleService lifecycle,
        WcsInventoryStore inventory,
        ModuleRunService modules,
        ILogger<TaskCompletionCoordinator> logger)
    {
        _stage = stage;
        _lifecycle = lifecycle;
        _inventory = inventory;
        _modules = modules;
        _logger = logger;
    }

    /// <summary>选点加锁后立即登记；Active=false 时只保管锁，不处理提前到达的 FINISHED。</summary>
    public void RegisterReservation(string taskId, string? startStorageTaskLockMark, string destinationMark)
    {
        var reservation = new Reservation(startStorageTaskLockMark, destinationMark, false);
        if (!_reservations.TryAdd(taskId, reservation))
            throw new InvalidOperationException($"任务 {taskId} 已登记资源占用");
    }

    /// <summary>GRCS 明确接收且库存已 MarkBusy 后激活完成处理。</summary>
    public void Activate(string taskId)
    {
        while (_reservations.TryGetValue(taskId, out var current))
        {
            if (current.Active) break;
            if (_reservations.TryUpdate(taskId, current with { Active = true }, current)) break;
        }

        // 防止 FINISHED 在登记和激活之间到达而丢失。
        var state = _lifecycle.GetState(taskId);
        if (state == WcsTaskState.Finished)
            QueueCompletion(taskId);
        else if (state == WcsTaskState.LoadFinished)
            OnLoadFinished(taskId);
    }

    /// <summary>下发失败或下发前终止：撤销登记并释放本次站点锁。</summary>
    public void CancelReservation(string taskId)
    {
        if (_reservations.TryRemove(taskId, out var reservation))
        {
            _inventory.ClearTaskLock(taskId, reservation.DestinationMark);
            _inventory.ClearTaskLock(taskId, reservation.StartStorageTaskLockMark);
        }
        _completionQueued.TryRemove(taskId, out _);
    }

    /// <summary>强制结束时释放当前进程登记的全部站点锁。</summary>
    public void ReleaseAllReservations()
    {
        foreach (var taskId in _reservations.Keys)
        {
            if (!_reservations.TryRemove(taskId, out var reservation)) continue;
            // 已被 GRCS 接收的任务在强制结束后仍可能执行；保留数据库预留，直到人工库存同步确认现场状态。
            if (!reservation.Active)
            {
                _inventory.ClearTaskLock(taskId, reservation.DestinationMark);
                _inventory.ClearTaskLock(taskId, reservation.StartStorageTaskLockMark);
            }
            _completionQueued.TryRemove(taskId, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stage.TaskLoadFinished += OnLoadFinished;
        _stage.TaskFinished += OnFinished;
        try
        {
            ResumeUnfinishedFinalizations();
            await ProcessLifecycleQueue(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _stage.TaskLoadFinished -= OnLoadFinished;
            _stage.TaskFinished -= OnFinished;
        }
    }

    private async Task ProcessLifecycleQueue(CancellationToken stoppingToken)
    {
        await foreach (var work in _lifecycleChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (work.Kind)
                {
                    case WorkKind.LoadFinished:
                        _inventory.OnTaskLoadFinished(work.TaskId);
                        break;
                    case WorkKind.CompleteReservation:
                        await CompleteReservationAsync(work.TaskId);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务生命周期收尾失败：{TaskId} {Kind}", work.TaskId, work.Kind);
            }
        }
    }

    private void OnLoadFinished(string taskId)
        => _lifecycleChannel.Writer.TryWrite(new WorkItem(WorkKind.LoadFinished, taskId));

    private void OnFinished(string taskId)
        => QueueCompletion(taskId);

    private void QueueCompletion(string taskId)
    {
        if (GetCompletionReservation(taskId) == null) return;
        if (_completionQueued.TryAdd(taskId, 0))
            _lifecycleChannel.Writer.TryWrite(new WorkItem(WorkKind.CompleteReservation, taskId));
    }

    private async Task CompleteReservationAsync(string taskId)
    {
        Reservation? reservation = null;
        if (_reservations.TryRemove(taskId, out var activeReservation))
            reservation = activeReservation.Active ? activeReservation : null;
        else
            reservation = GetCompletionReservation(taskId);

        if (reservation == null)
        {
            _completionQueued.TryRemove(taskId, out _);
            return;
        }

        try
        {
            _stage.TryRecordSystemEvent(taskId, "WCS_FINALIZING", true, 0);
            var inventoryFinalized = _stage.GetAll().Any(record => record.TaskId == taskId
                && record.Stage == "WCS_INVENTORY_FINALIZED" && record.IsSuccess);
            var sorting = new WcsInventoryStore.SortingCompletionResult(false, false, reservation.DestinationMark, "");
            if (!inventoryFinalized)
            {
                sorting = _inventory.TryReleaseToSortingChild(taskId, reservation.DestinationMark);
                if (sorting.IsSortingParent)
                {
                    if (!sorting.Success)
                    {
                        _inventory.ClearTaskLock(taskId, reservation.StartStorageTaskLockMark);
                        _stage.TryRecordSystemEvent(taskId, "SORTING_SLOT_ASSIGN_FAILED", false, 0);
                        FinalizationFailed(taskId, sorting.Message);
                        return;
                    }

                    _stage.UpdateEndStationCode(taskId, sorting.DestinationMark);
                    _stage.TryRecordSystemEvent(taskId, $"SORTING_SLOT_ASSIGNED:{sorting.DestinationMark}", true, 0);
                }
                else
                {
                    _inventory.Release(taskId, reservation.DestinationMark);
                    var releaseFailed = _stage.GetAll().Any(record => record.TaskId == taskId
                        && record.Stage.StartsWith("INVENTORY_RELEASE_FAILED:", StringComparison.OrdinalIgnoreCase));
                    if (releaseFailed)
                    {
                        _inventory.ClearTaskLock(taskId, reservation.StartStorageTaskLockMark);
                        FinalizationFailed(taskId, "终点库存收尾失败");
                        return;
                    }
                }
                _stage.TryRecordSystemEvent(taskId, "WCS_INVENTORY_FINALIZED", true, 0);
            }

            _inventory.ClearTaskLock(taskId, reservation.StartStorageTaskLockMark);
            if (!await _modules.RunEndModulesAsync(taskId))
            {
                FinalizationFailed(taskId, "终点后模块或库存效果失败");
                return;
            }

            _stage.TryRecordSystemEvent(taskId, "WCS_COMPLETED", true, 0);
            _logger.LogInformation("WCS 任务完成：{TaskId} -> {Destination}", taskId,
                sorting.IsSortingParent && sorting.Success ? sorting.DestinationMark : reservation.DestinationMark);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WCS 任务收尾失败：{TaskId}", taskId);
            FinalizationFailed(taskId, ex.Message);
        }
        finally
        {
            _completionQueued.TryRemove(taskId, out _);
        }
    }

    /// <summary>
    /// 活跃任务优先使用进程内预留；服务重启后从已下发的 CREATED 记录恢复路线。
    /// CREATED 的 StageStatus 由下发结果写入，避免未下发任务的异常回调释放储位。
    /// </summary>
    private Reservation? GetCompletionReservation(string taskId)
    {
        if (_reservations.TryGetValue(taskId, out var reservation))
            return reservation.Active ? reservation : null;

        var created = _stage.GetAll().LastOrDefault(record => record.IsCreated
            && record.IsSuccess
            && string.Equals(record.TaskId, taskId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(record.EndStationCode));
        return created == null
            ? null
            : new Reservation(ToMapMark(created.StartStationCode), ToMapMark(created.EndStationCode), true);
    }

    private static string ToMapMark(string stationCode)
    {
        var value = stationCode.Trim();
        var separator = value.LastIndexOf('_');
        return separator > 0 && int.TryParse(value[(separator + 1)..], out _)
            ? value[..separator]
            : value;
    }

    private void FinalizationFailed(string taskId, string reason)
    {
        _stage.TryRecordSystemEvent(taskId, "WCS_FINALIZATION_FAILED", false, 0);
        _logger.LogWarning("WCS 任务收尾失败：{TaskId} {Reason}", taskId, reason);
    }

    private void ResumeUnfinishedFinalizations()
    {
        var records = _stage.GetAll();
        foreach (var taskId in records.Where(record => record.Stage == "FINISHED")
                     .Select(record => record.TaskId).Where(taskId => !string.IsNullOrWhiteSpace(taskId))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var isFinal = records.Any(record => record.TaskId == taskId
                && (record.Stage == "WCS_COMPLETED" || record.Stage == "WCS_FINALIZATION_FAILED"));
            if (!isFinal) QueueCompletion(taskId);
        }
    }

    private sealed record Reservation(string? StartStorageTaskLockMark, string DestinationMark, bool Active);
    private readonly record struct WorkItem(WorkKind Kind, string TaskId);
    private enum WorkKind { LoadFinished, CompleteReservation }
}
