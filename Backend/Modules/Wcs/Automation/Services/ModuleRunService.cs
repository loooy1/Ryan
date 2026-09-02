using System.Text.Json;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Proxy.Services;
using Microsoft.Extensions.Logging;

namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 统一模块执行引擎（取代前端 ModuleRunnerService + TaskDispatch 客户端模块执行）。
/// 所有任务（手动 + 自动化）的三类模块都在后端执行：
/// 起点之前(Start.BeforeModules) 在下发前、起点之后(Start.AfterModules) 在任务 CREATED 之后、终点之后(End.AfterModules) 在任务 FINISHED 后。
/// 起点之后改为「先写 CREATED 创建行、再执行」，确保货物到达等模板一定在 CREATED 阶段之后才跑。
/// 执行结果写入 ModuleExecLogStore（供前端「模块执行记录」面板轮询）。
/// 与 AutoTemplateRunner / FinishedModuleWatcher 共用，避免重复逻辑。
/// </summary>
public class ModuleRunService
{
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly TaskTemplateStore _taskTemplates;
    private readonly FeatureModuleStore _modules;
    private readonly ITaskStageService _stage;
    private readonly ModuleExecLogStore _execLog;
    private readonly ILogger<ModuleRunService> _logger;
    private readonly AutomationLogService _autoLog;
    private readonly MockRuleStore _mocks;

    // 下发成功后、执行「起点之后」模块前的延时（毫秒）。GRCS 接收任务需要时间，保留。
    private const int TestAfterDispatchDelayMs = 2000;

    public ModuleRunService(GrcsHttpClient grcs, WcsSettingsService settings, TaskTemplateStore taskTemplates,
        FeatureModuleStore modules, ITaskStageService stage, ModuleExecLogStore execLog, ILogger<ModuleRunService> logger,
        AutomationLogService autoLog, MockRuleStore mocks)
    {
        _grcs = grcs; _settings = settings; _taskTemplates = taskTemplates; _modules = modules;
        _stage = stage; _execLog = execLog; _logger = logger; _autoLog = autoLog; _mocks = mocks;
    }

    /// <summary>模块执行上下文（参数解析用）。</summary>
    public class ModuleCtx
    {
        public string Start = "";
        public string End = "";
        public string Container = "";
        public string Warehouse = "";
        public string TaskType = "";
        public string TaskId = "";
    }

    /// <summary>
    /// 下发任务组 + 执行其模板的起点/起点之后模块：
    /// 起点模块（下发前）→ 下发（GRCS /api/v1/task_receive）→ 起点之后模块（下发成功且 body.success 后）。
    /// 返回下发结果（ok/code/json）；模块执行结果不在此返回，统一进 ModuleExecLogStore。
    /// </summary>
    public async Task<(bool ok, int code, string json)> SendTaskWithModulesAsync(WcsTaskGroup group, string? roundId = null)
    {
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            return (false, 0, "未配置 GRCS 地址（连接设置页填写）");
        // 任务阶段未配置 Mock 卡时禁止下发：下发后阶段无法上报，任务会永久卡住
        if (!_mocks.HasTaskStageRule())
            return (false, 0, "未配置任务阶段卡（Mock 卡片勾选「关联任务看板」），禁止下发任务，请先在信号交互→通用 Mock 入站配置");
        var task = group.Tasks.FirstOrDefault();
        if (task == null) return (false, 0, "任务组为空");
        var tpl = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, task.TaskType, StringComparison.OrdinalIgnoreCase));
        var ctx = BuildCtxFromGroup(group, task);

        // 起点之前模块（下发前）
        if (tpl != null) await RunModules("起点之前", tpl.Start?.BeforeModules ?? [], ctx, roundId);

        // 下发
        if (roundId != null)
            _autoLog.Add(roundId, "下发数据(GRCS task_receive): " + JsonSerializer.Serialize(group), "#93c5fd");
        var (ok, code, json) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, group);

        // 下发成功 → 先写创建行(CREATED)，再执行「起点之后」模块；
        // 确保货物到达等「起点之后」模板一定在任务 CREATED 阶段之后才跑（而非仅 HTTP 响应成功即跑）。
        // 创建行由后端（WCS 下发权威方）统一写，手动/自动化共用；前端稍后的 AppendAsync 会因 taskId 去重而跳过。
        if (ok && tpl != null && TryGetBodySuccess(json))
        {
            try
            {
                if (task != null)
                {
                    _stage.RecordCreated([new TaskLedgerEntry
                    {
                        TaskId = task.TaskId,
                        TaskType = task.TaskType,
                        ContainerCode = task.ContainerCode,
                        CargoCode = "",
                        StationCode = task.StationCode,
                        Warehouse = group.Warehouse,
                        Time = DateTime.Now.ToString("O"),
                        Ok = true,
                        StatusCode = code,
                    }]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Module] 写创建行(CREATED)失败：{Msg}", ex.Message);
            }
            if (roundId != null) _autoLog.Add(roundId, $"下发GRCS完成（{task.TaskId}），等待2s", "#4ade80");
            // 下发成功 → 执行「起点之后」模块（GRCS 接收任务需时间，保留 2 秒延时）
            await Task.Delay(TestAfterDispatchDelayMs);
            await RunModules("起点之后", tpl.Start?.AfterModules ?? [], ctx, roundId);
        }

        return (ok, code, json);
    }

    /// <summary>执行任务的终点(End)模块。preset 非空（自动化）直接用；否则从台账创建行推导上下文。</summary>
    public async Task RunEndModulesAsync(string taskId, ModuleCtx? preset = null, string? roundId = null)
    {
        var ctx = preset ?? BuildCtxFromRecord(taskId);
        if (ctx == null) return;
        var tpl = _taskTemplates.GetAll().FirstOrDefault(t => string.Equals(t.Value, ctx.TaskType, StringComparison.OrdinalIgnoreCase));
        if (tpl == null) return;
        var endIds = tpl.End?.AfterModules ?? [];
        if (endIds.Count == 0) return;
        await RunModules("终点", endIds, ctx, roundId);
    }

    private async Task RunModules(string point, List<string> moduleIds, ModuleCtx ctx, string? roundId = null)
    {
        foreach (var id in moduleIds)
        {
            var mod = _modules.GetAll().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (mod == null)
            {
                _execLog.Add(ctx.TaskId, point, "模块未找到:" + id, false, 0, "");
                continue;
            }
            if (string.IsNullOrWhiteSpace(mod.ApiUrl))
            {
                _execLog.Add(ctx.TaskId, point, mod.Name, false, 0, "API 地址为空，已跳过");
                continue;
            }
            var body = new Dictionary<string, object?>();
            foreach (var p in mod.Params) body[p.Name] = ResolveSource(p, ctx);
            var url = (_settings.Get()?.GrcsBaseUrl ?? "").TrimEnd('/') + mod.ApiUrl;
            if (roundId != null)
                _autoLog.Add(roundId, $"▶ 执行模块「{mod.Name}」POST {url}", "#93c5fd");
            var (ok, code, json) = await _grcs.ForwardAsync(url, HttpMethod.Post, JsonSerializer.Serialize(body));
            _execLog.Add(ctx.TaskId, point, mod.Name, ok, code, json);
            if (roundId != null)
                _autoLog.Add(roundId, $"{(ok ? "✓" : "✗")} 模块「{mod.Name}」HTTP {code}：{json[..Math.Min(json.Length, 200)]}", ok ? "#4ade80" : "#f87171");
            _logger.LogInformation("[Module] {TaskId} {Point} {Module}: {Ok} {Code} {Json}", ctx.TaskId, point, mod.Name, ok, code, json.Length > 200 ? json[..200] : json);
        }
    }

    /// <summary>
    /// 重试单条模块执行记录（前端「模块执行记录」→ 重试按钮）：用任务创建行恢复上下文，
    /// 按记录中的模块名找到模块配置后重新 POST（MsgTime 等 Now 参数用当前时间），新记录经 SignalR 实时推送。
    /// </summary>
    public async Task RetryEntryAsync(ModuleExecLogEntry entry)
    {
        var ctx = BuildCtxFromRecord(entry.TaskId);
        if (ctx == null)
        {
            _execLog.Add(entry.TaskId, entry.Point, entry.Module, false, 0, "任务上下文不存在（创建行缺失），无法重试");
            return;
        }
        var mod = _modules.GetAll().FirstOrDefault(m => string.Equals(m.Name, entry.Module, StringComparison.OrdinalIgnoreCase));
        if (mod == null)
        {
            _execLog.Add(entry.TaskId, entry.Point, entry.Module, false, 0, "模块未找到，无法重试");
            return;
        }
        if (string.IsNullOrWhiteSpace(mod.ApiUrl))
        {
            _execLog.Add(entry.TaskId, entry.Point, entry.Module, false, 0, "API 地址为空，已跳过");
            return;
        }
        var body = new Dictionary<string, object?>();
        foreach (var p in mod.Params) body[p.Name] = ResolveSource(p, ctx);
        var url = (_settings.Get()?.GrcsBaseUrl ?? "").TrimEnd('/') + mod.ApiUrl;
        var (ok, code, json) = await _grcs.ForwardAsync(url, HttpMethod.Post, JsonSerializer.Serialize(body));
        _execLog.Add(entry.TaskId, entry.Point, mod.Name, ok, code, json);
        _logger.LogInformation("[Module] 重试 {TaskId} {Point} {Module}: {Ok} {Code} {Json}", entry.TaskId, entry.Point, mod.Name, ok, code, json.Length > 200 ? json[..200] : json);
    }

    private static ModuleCtx? BuildCtxFromGroup(WcsTaskGroup group, WcsTaskItem task) => new()
    {
        Start = task.StationCode.FirstOrDefault() ?? "",
        End = task.StationCode.LastOrDefault() ?? "",
        Container = task.ContainerCode,
        Warehouse = group.Warehouse,
        TaskType = task.TaskType,
        TaskId = task.TaskId,
    };

    private ModuleCtx? BuildCtxFromRecord(string taskId)
    {
        var rec = _stage.GetAll().FirstOrDefault(r => r.IsCreated && string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        if (rec == null) return null;
        return new ModuleCtx
        {
            Start = rec.RouteCodes.FirstOrDefault() ?? "",
            End = rec.RouteCodes.LastOrDefault() ?? "",
            Container = rec.ContainerCode,
            Warehouse = rec.Warehouse,
            TaskType = rec.TaskType,
            TaskId = rec.TaskId,
        };
    }

    private static object? ResolveSource(WorkParamDto p, ModuleCtx ctx) => p.Source switch
    {
        WorkValueSourceDto.StartPoint => ctx.Start,
        WorkValueSourceDto.EndPoint => ctx.End,
        WorkValueSourceDto.TaskContainer => ctx.Container,
        WorkValueSourceDto.TaskWarehouse => ctx.Warehouse,
        WorkValueSourceDto.TaskType => ctx.TaskType,
        WorkValueSourceDto.TaskId => ctx.TaskId,
        WorkValueSourceDto.Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => p.FixedValue,
    };

    private static bool TryGetBodySuccess(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
}
