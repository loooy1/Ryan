using WCSBackend.Modules.Wcs.Proxy.Services;
using Contracts.Dtos;
using Contracts.Entities;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Automation.Services;
using WCSBackend.Modules.Wcs.Console.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using WCSBackend.Modules.Wcs.Realtime;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// 自动化控制台接口（/api/wcs/auto/*，供前端遥控；不是 GRCS 协议接口）。
/// 启停/参数/状态/日志（sinceId 增量）/选点范围/自动化模板 CRUD/单次执行/移动任务循环/信号自动开关。
/// 旧的两段式轮询（AutoRunHostedService）与批量容器（ContainerTaskRunner）已移除，统一由
/// AutoTemplateRunner + 用户自定义自动化模板取代。
/// </summary>
[ApiController]
[Route("api/wcs/auto")]
public class AutomationConsoleController : ControllerBase
{
    private readonly AutoTemplateRunner _auto;
    private readonly AutoTemplateStore _templates;
    private readonly AutomationLogService _logs;
    private readonly RangeConfigService _rangeConfig;
    private readonly WcsSettingsService _settings;
    private readonly MoveLoopRunner _moveLoop;
    private readonly NestRunner _nest;
    private readonly NestConfigService _nestConfig;
    private readonly GrcsHttpClient _grcs;
    private readonly WcsInventoryStore _inventory;
    private readonly ITaskStageService _taskStages;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public AutomationConsoleController(AutoTemplateRunner auto, AutoTemplateStore templates, AutomationLogService logs,
        RangeConfigService rangeConfig, WcsSettingsService settings,
        MoveLoopRunner moveLoop, NestRunner nest, NestConfigService nestConfig, GrcsHttpClient grcs, WcsInventoryStore inventory,
        ITaskStageService taskStages, IHubContext<TaskStageRealtimeHub> hub)
    {
        _auto = auto;
        _templates = templates;
        _logs = logs;
        _rangeConfig = rangeConfig;
        _settings = settings;
        _moveLoop = moveLoop;
        _nest = nest;
        _nestConfig = nestConfig;
        _grcs = grcs;
        _inventory = inventory;
        _taskStages = taskStages;
        _hub = hub;
    }

    /// <summary>库存分类汇总 + 明细（纯空托 / 带货托 / 纯货物 / 锁定中=移动单元数），按「以前逻辑」在后端统计。</summary>
    /// <summary>整体状态快照（前端 2s 轮询）。dispatchActive=任一下发模式进行中（前端跨标签页警示/禁用判断）。</summary>
    /// <summary>Inventory summary from WCS slots only, filtered by the active automation range.</summary>
    [HttpGet("inventory-summary")]
    public ActionResult<InventorySummaryDto> InventorySummary()
    {
        var dto = new InventorySummaryDto();
        var range = _rangeConfig.Get();
        var selectedMarks = range.Enabled && range.Marks.Count > 0
            ? range.Marks.Where(mark => !string.IsNullOrWhiteSpace(mark))
                .Select(mark => mark.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var slots = _inventory.Slots();
        var slotsByMark = slots.ToDictionary(slot => slot.Mark, StringComparer.OrdinalIgnoreCase);

        bool IsInsideActiveRange(string mark)
        {
            if (!range.Enabled) return true;
            if (selectedMarks != null && !selectedMarks.Contains(mark)) return false;
            return range.FloorFilter == 0
                || (slotsByMark.TryGetValue(mark, out var slot) && slot.Floor == range.FloorFilter);
        }

        foreach (var slot in slots)
        {
            if (!IsInsideActiveRange(slot.Mark)) continue;

            var hasPallet = !string.IsNullOrWhiteSpace(slot.PalletCode);
            var hasCargo = !string.IsNullOrWhiteSpace(slot.CargoCode);
            if (!hasPallet && !hasCargo) continue;

            var palletTransit = hasPallet && string.Equals(slot.PalletStatus, "transit", StringComparison.OrdinalIgnoreCase);
            var cargoTransit = hasCargo && string.Equals(slot.CargoStatus, "transit", StringComparison.OrdinalIgnoreCase);
            var palletLocked = hasPallet && slot.PalletStatus is "picked" or "fail";
            var cargoLocked = hasCargo && slot.CargoStatus is "picked" or "fail";
            var status = palletTransit || palletLocked ? slot.PalletStatus : cargoTransit || cargoLocked ? slot.CargoStatus : null;
            var detail = ToInventoryDetail(slot, status);

            if (palletTransit || cargoTransit)
            {
                dto.TransitItems.Add(detail);
                continue;
            }
            if (string.Equals(slot.SiteType, "Terminal", StringComparison.OrdinalIgnoreCase))
            {
                dto.TerminalItems.Add(detail);
                continue;
            }
            if (palletLocked || cargoLocked || !string.IsNullOrWhiteSpace(slot.TaskLockId))
            {
                dto.LockedItems.Add(detail);
                continue;
            }
            if (hasPallet && hasCargo) dto.LoadedItems.Add(detail);
            else if (hasPallet) dto.EmptyItems.Add(detail);
            else dto.CargoItems.Add(detail);
        }

        // LOAD_FINISH clears the source row in wcs_slots. The remaining transit
        // record is persisted in task_records until RCS reports FINISHED/cancelled.
        var stages = _taskStages.GetAll();
        var finishedTaskIds = stages
            .Where(record => record.Stage is "FINISHED" or "CANCELLED")
            .Select(record => record.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slotTransitCodes = dto.TransitItems
            .Select(item => item.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var transit in stages
            .Where(record => record.Stage.StartsWith("TRANSIT:", StringComparison.OrdinalIgnoreCase)
                && !finishedTaskIds.Contains(record.TaskId))
            .GroupBy(record => record.TaskId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.Time).First()))
        {
            var startMark = ToMapMark(transit.StartStationCode);
            var endMark = ToMapMark(transit.EndStationCode);
            if (!IsInsideActiveRange(startMark) && !IsInsideActiveRange(endMark)) continue;
            if (string.IsNullOrWhiteSpace(transit.ContainerCode) || !slotTransitCodes.Add(transit.ContainerCode)) continue;

            dto.TransitItems.Add(new InventoryDetailItem
            {
                Code = transit.ContainerCode,
                CargoCode = string.IsNullOrWhiteSpace(transit.CargoCode) ? null : transit.CargoCode,
                Station = string.IsNullOrWhiteSpace(endMark) ? startMark : $"{startMark} -> {endMark}",
                TaskId = transit.TaskId,
                Status = "transit",
            });
        }

        dto.Empty = dto.EmptyItems.Count;
        dto.Loaded = dto.LoadedItems.Count;
        dto.Cargo = dto.CargoItems.Count;
        dto.Locked = dto.LockedItems.Count;
        dto.Transit = dto.TransitItems.Count;
        dto.Terminal = dto.TerminalItems.Count;
        return Ok(dto);
    }

    private static string ToMapMark(string stationCode)
        => stationCode.Length > 2 && stationCode[^2..] is "_0" or "_1"
            ? stationCode[..^2]
            : stationCode;

    private static InventoryDetailItem ToInventoryDetail(WcsSlotRow slot, string? status)
        => new()
        {
            Code = !string.IsNullOrWhiteSpace(slot.PalletCode) ? slot.PalletCode : slot.CargoCode,
            CargoCode = !string.IsNullOrWhiteSpace(slot.PalletCode) && !string.IsNullOrWhiteSpace(slot.CargoCode) ? slot.CargoCode : null,
            Station = slot.Mark,
            Status = status,
        };

    [HttpGet("status")]
    public ActionResult<object> Status()
    {
        var tpl = _auto.Snapshot();
        return Ok(new
        {
            running = tpl.Running,
            autoTabId = tpl.ActiveTabId,
            interval = tpl.Interval,
            activeTemplateId = tpl.ActiveTemplateId,
            activeTemplateName = tpl.ActiveTemplateName,
            executed = tpl.Executed,
            status = tpl.Status,
            dispatchActive = tpl.AnyRunning,
            moveRunning = _moveLoop.Running,
            moveTabId = _moveLoop.TabId,
            moveTotal = _moveLoop.Total,
            moveOk = _moveLoop.Ok,
            moveFail = _moveLoop.Fail,
            moveLastError = _moveLoop.LastError,
            templates = _templates.GetAll(),
            settings = _settings.Get(),
            nestRunning = _nest.Running,
        });
    }

    [HttpPost("start")]
    public ActionResult<object> Start([FromBody] StartRequest? req)
    {
        _auto.ChainTemplates = req?.ChainTemplates ?? false;
        var (ok, reason) = _auto.Start(req?.TabId, req?.TemplateIds);
        return Ok(new { success = ok, message = reason ?? "", running = _auto.Running });
    }

    [HttpPost("stop")]
    public ActionResult<object> Stop()
    {
        _auto.Stop();
        return Ok(new { running = false });
    }

    [HttpPost("force-stop")]
    public ActionResult<object> ForceStop()
    {
        _auto.ForceEnd();
        return Ok(new { success = true });
    }

    [HttpPost("interval")]
    public ActionResult<object> SetInterval([FromBody] IntervalRequest req)
    {
        _auto.SetInterval(req.Interval * 1000);
        return Ok(new { interval = _auto.Interval });
    }

    // ── 自动化模板 CRUD（跨浏览器共享，存 auto_templates 表）──

    /// <summary>单条保存（新建或更新），只动当前模板，不影响其他行。</summary>
    [HttpPut("templates/{id}")]
    public ActionResult<object> SaveTemplate([FromRoute] string id, [FromBody] AutoTemplateDto item)
    {
        if (item == null) return BadRequest(new { success = false, errors = new[] { "模板内容为空" } });
        item.Id = id;
        var errors = _auto.ValidateTemplates([item]);
        if (errors.Count > 0)
            return BadRequest(new { success = false, errors });
        _templates.Upsert(item);
        return Ok(new { success = true, count = _templates.GetAll().Count });
    }

    [HttpDelete("templates")]
    public ActionResult<object> DeleteTemplate([FromQuery] string id)
    {
        var ok = _templates.Remove(id);
        return Ok(new { success = ok });
    }

    /// <summary>按轮次分组的日志（每轮一个标题，含该轮所有条目）。</summary>
    [HttpGet("logs")]
    public ActionResult<object> Logs() => Ok(_logs.GetRounds());

    [HttpDelete("logs")]
    public ActionResult<object> ClearLogs() { _logs.Clear(); return Ok(new { success = true }); }

    [HttpDelete("logs/system")]
    public ActionResult<object> ClearSystemLogs() { _logs.ClearSystem(); return Ok(new { success = true }); }

    [HttpGet("range")]
    public ActionResult<RangeConfigDto> Range() => Ok(_rangeConfig.Get());

    [HttpPut("range")]
    public ActionResult<object> SaveRange([FromBody] RangeConfigDto range)
    {
        _rangeConfig.Set(range);
        _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "range");
        return Ok(new { success = true });
    }

    [HttpGet("settings")]
    public ActionResult<WcsSettingsDto> Settings() => Ok(_settings.Get());

    [HttpPut("settings")]
    public ActionResult<object> SaveSettings([FromBody] WcsSettingsDto settings)
    {
        _settings.Set(settings);
        _ = _hub.Clients.All.SendAsync("ConfigurationChanged", "settings");
        return Ok(new { success = true });
    }

    /// <summary>纯移动任务循环启动：前端只通知「开启」，循环/选点/统计/日志全在 MoveLoopRunner（后端）。
    /// 互斥（模板轮询/其他标签页）由 AutomationGate 判定。</summary>
    [HttpPost("move/start")]
    public ActionResult<object> MoveStart([FromBody] StartMoveReq req)
    {
        var (ok, reason) = _moveLoop.Start(req ?? new StartMoveReq());
        return Ok(new { success = ok, reason });
    }

    /// <summary>纯移动任务循环停止：取消循环、写汇总日志、释放互斥。</summary>
    [HttpPost("move/stop")]
    public ActionResult<object> MoveStop([FromBody] TabRequest req)
    {
        _moveLoop.Stop(req?.TabId);
        return Ok(new { success = true });
    }

    /// <summary>信号自动开关（进入申请/到达/移除/分拣四档，字段可缺省：只改传了的档）。</summary>
    // ── 归巢模式（地图框选巢区 → 持续调度：区域外就绪车逐台 MOVE_ONLY 到区内空点，直到点全被占用）──

    [HttpGet("nest/config")]
    public ActionResult<NestConfigDto> NestConfig() => Ok(_nestConfig.Get());

    [HttpPut("nest/config")]
    public ActionResult<object> SaveNestConfig([FromBody] NestConfigDto config)
    {
        _nestConfig.Set(config ?? new NestConfigDto());
        return Ok(new { success = true });
    }

    /// <summary>执行归巢（后台持续调度，运行中重复调用被忽略）。Vehicles = 本次车队（前端多选车名，可空 = 自动捕获当前就绪车）。</summary>
    [HttpPost("nest/run")]
    public ActionResult<object> NestRun([FromBody] NestRunRequest? req)
    {
        var (ok, reason) = _nest.Run(req?.Vehicles);
        return Ok(new { success = ok, reason });
    }

    /// <summary>GRCS 全部车辆（归巢车辆多选用；含就绪状态 IsReady）。</summary>
    [HttpGet("vehicles")]
    public async Task<ActionResult<object>> Vehicles()
    {
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            return BadRequest(new { ok = false, reason = "未配置 GRCS 地址（地图信息页系统设置）" });
        var (ok, code, json) = await _grcs.QueryVehiclesAsync(settings.GrcsBaseUrl, settings.SceneName);
        if (!ok)
            return BadRequest(new { ok = false, code, reason = code == 0 ? "查询车辆超时/网络异常" : $"查询车辆 HTTP {code}" });
        try
        {
            var vehicles = JsonSerializer.Deserialize<List<VehicleInfoDto>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) ?? [];
            return Ok(new { ok = true, vehicles });
        }
        catch (Exception ex)
        {
            return BadRequest(new { ok = false, reason = $"车辆解析失败：{ex.Message}" });
        }
    }

    /// <summary>停止归巢（中断等待与后续下发，已下发的不撤销）。</summary>
    [HttpPost("nest/stop")]
    public ActionResult<object> NestStop()
    {
        _nest.Stop();
        return Ok(new { success = true });
    }
}

public class IntervalRequest { public int Interval { get; set; } } // 秒
public class NestRunRequest { public List<string>? Vehicles { get; set; } } // 本次归巢车队（可空 = 自动捕获）
public class StartRequest { public string? TabId { get; set; } public List<string>? TemplateIds { get; set; } public bool ChainTemplates { get; set; } }
public class TabRequest { public string? TabId { get; set; } }
