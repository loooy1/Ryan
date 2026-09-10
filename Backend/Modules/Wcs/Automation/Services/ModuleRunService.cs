using System.Text.Json;
using Contracts.Dtos;
using Microsoft.Extensions.Logging;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Proxy.Services;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>统一运行任务模块，并将每次结果写入 task_records。</summary>
public class ModuleRunService
{
    private const int TestAfterDispatchDelayMs = 2000;
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly TaskTemplateStore _templates;
    private readonly FeatureModuleStore _modules;
    private readonly ITaskStageService _stages;
    private readonly ILogger<ModuleRunService> _logger;
    private readonly AutomationLogService _logs;
    private readonly MockRuleStore _mocks;
    private readonly TaskLifecycleService _lifecycle;

    public ModuleRunService(GrcsHttpClient grcs, WcsSettingsService settings, TaskTemplateStore templates,
        FeatureModuleStore modules, ITaskStageService stages, ILogger<ModuleRunService> logger,
        AutomationLogService logs, MockRuleStore mocks, TaskLifecycleService lifecycle)
    {
        _grcs = grcs; _settings = settings; _templates = templates; _modules = modules;
        _stages = stages; _logger = logger; _logs = logs; _mocks = mocks; _lifecycle = lifecycle;
    }

    public class ModuleCtx
    {
        public string Start = "";
        public string End = "";
        public string Container = "";
        public string Warehouse = "";
        public string TaskType = "";
        public string TaskId = "";
    }

    public async Task<(bool ok, int code, string json)> SendTaskWithModulesAsync(WcsTaskGroup group, string? roundId = null)
    {
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            return (false, 0, "未配置 GRCS 地址（连接设置页填写）");
        if (!_mocks.HasTaskStageRule())
            return (false, 0, "未配置任务阶段卡，禁止下发任务");
        var task = group.Tasks.FirstOrDefault();
        if (task == null) return (false, 0, "任务组为空");

        var template = _templates.GetAll().FirstOrDefault(t =>
            string.Equals(t.Value, task.TaskType, StringComparison.OrdinalIgnoreCase));
        var ctx = BuildCtxFromGroup(group, task);
        _stages.RecordCreated([new TaskLedgerEntry
        {
            TaskId = task.TaskId, TaskType = task.TaskType, ContainerCode = task.ContainerCode,
            StationCode = task.StationCode, Warehouse = group.Warehouse, Time = DateTime.Now.ToString("O"),
            Ok = false, StatusCode = 0,
        }]);

        if (template != null && !await RunModulesAsync("BEFORE_MODULE", template.Start?.BeforeModules ?? [], ctx, roundId, true))
        {
            _stages.TryRecordSystemEvent(task.TaskId, "CANCELLED", false, -1);
            return (false, -1, "起点之前模块执行失败，任务未下发");
        }

        if (roundId != null)
            _logs.Add(roundId, "下发数据(GRCS task_receive): " + JsonSerializer.Serialize(group), "#93c5fd");
        _lifecycle.BeginDispatch(task.TaskId);
        var (ok, code, json) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, group);
        var accepted = ok && IsAccepted(json);
        if (!accepted)
        {
            _lifecycle.DispatchFailed(task.TaskId, code == 0);
            _stages.RecordDispatchResult(task.TaskId, false, code);
            _stages.TryRecordSystemEvent(task.TaskId, "CANCELLED", false, code);
            return (false, code, json);
        }

        _stages.RecordDispatchResult(task.TaskId, true, code);
        if (roundId != null) _logs.Add(roundId, $"下发 GRCS 完成（{task.TaskId}），等待 2 秒", "#4ade80");
        if (template != null)
        {
            await Task.Delay(TestAfterDispatchDelayMs);
            await RunModulesAsync("AFTER_START_MODULE", template.Start?.AfterModules ?? [], ctx, roundId);
        }
        return (true, code, json);
    }

    public async Task RunEndModulesAsync(string taskId, ModuleCtx? preset = null, string? roundId = null)
    {
        var ctx = preset ?? BuildCtxFromRecord(taskId);
        if (ctx == null) return;
        var template = _templates.GetAll().FirstOrDefault(t =>
            string.Equals(t.Value, ctx.TaskType, StringComparison.OrdinalIgnoreCase));
        if (template != null)
            await RunModulesAsync("AFTER_END_MODULE", template.End?.AfterModules ?? [], ctx, roundId);
    }

    private async Task<bool> RunModulesAsync(string prefix, List<string> ids, ModuleCtx ctx,
        string? roundId = null, bool stopOnFailure = false)
    {
        var allOk = true;
        foreach (var id in ids)
        {
            var module = _modules.GetAll().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (module == null || string.IsNullOrWhiteSpace(module.ApiUrl))
            {
                _stages.TryRecordSystemEvent(ctx.TaskId, $"{prefix}:{id}", false, 0);
                allOk = false;
                if (stopOnFailure) return false;
                continue;
            }
            var body = new Dictionary<string, object?>();
            foreach (var parameter in module.Params) body[parameter.Name] = Resolve(parameter, ctx);
            var url = (_settings.Get()?.GrcsBaseUrl ?? "").TrimEnd('/') + module.ApiUrl;
            if (roundId != null) _logs.Add(roundId, $"▶ 执行模块「{module.Name}」POST {url}", "#93c5fd");
            var (ok, code, json) = await _grcs.ForwardAsync(url, HttpMethod.Post, JsonSerializer.Serialize(body));
            _stages.TryRecordSystemEvent(ctx.TaskId, $"{prefix}:{id}", ok, code);
            if (roundId != null)
                _logs.Add(roundId, $"{(ok ? "✓" : "✗")} 模块「{module.Name}」HTTP {code}：{json[..Math.Min(json.Length, 200)]}", ok ? "#4ade80" : "#f87171");
            _logger.LogInformation("[Module] {TaskId} {Prefix} {Module}: {Ok} {Code}", ctx.TaskId, prefix, module.Name, ok, code);
            if (!ok) { allOk = false; if (stopOnFailure) return false; }
        }
        return allOk;
    }

    private static ModuleCtx BuildCtxFromGroup(WcsTaskGroup group, WcsTaskItem task) => new()
    {
        Start = task.StationCode.FirstOrDefault() ?? "", End = task.StationCode.LastOrDefault() ?? "",
        Container = task.ContainerCode, Warehouse = group.Warehouse, TaskType = task.TaskType, TaskId = task.TaskId,
    };

    private ModuleCtx? BuildCtxFromRecord(string taskId)
    {
        var record = _stages.GetAll().FirstOrDefault(r => r.IsCreated && string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        return record == null ? null : new ModuleCtx
        {
            Start = record.RouteCodes.FirstOrDefault() ?? "", End = record.RouteCodes.LastOrDefault() ?? "",
            Container = record.ContainerCode, Warehouse = record.Warehouse, TaskType = record.TaskType, TaskId = record.TaskId,
        };
    }

    private static object? Resolve(WorkParamDto parameter, ModuleCtx ctx) => parameter.Source switch
    {
        WorkValueSourceDto.StartPoint => ctx.Start, WorkValueSourceDto.EndPoint => ctx.End,
        WorkValueSourceDto.TaskContainer => ctx.Container, WorkValueSourceDto.TaskWarehouse => ctx.Warehouse,
        WorkValueSourceDto.TaskType => ctx.TaskType, WorkValueSourceDto.TaskId => ctx.TaskId,
        WorkValueSourceDto.Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), _ => parameter.FixedValue,
    };

    private static bool IsAccepted(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("success", out var value) && value.ValueKind == JsonValueKind.True; }
        catch { return false; }
    }
}
