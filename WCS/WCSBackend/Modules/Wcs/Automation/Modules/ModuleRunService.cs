using System.Text.Json;
using System.Collections.Concurrent;
using Contracts.Dtos;
using Microsoft.Extensions.Logging;
using WCSBackend.Modules.Wcs.Console.Services;
using WCSBackend.Modules.Wcs.Infrastructure;
using WCSBackend.Modules.Wcs.Proxy.Services;

namespace WCSBackend.Modules.Wcs.Automation.Services;

/// <summary>Runs task modules and records each result in task_records.</summary>
public class ModuleRunService
{
    private const int TestAfterDispatchDelayMs = 2000;
    private readonly GrcsHttpClient _grcs;
    private readonly WcsSettingsService _settings;
    private readonly TaskTemplateStore _templates;
    private readonly RcsProtocolTemplateStore _protocols;
    private readonly ITaskStageService _stages;
    private readonly ILogger<ModuleRunService> _logger;
    private readonly AutomationLogService _logs;
    private readonly MockRuleStore _mocks;
    private readonly TaskLifecycleService _lifecycle;
    private readonly ModuleEffectService _effects;
    private readonly ModuleExecutionLogStore _moduleLogs;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _workflowCompletions = new(StringComparer.OrdinalIgnoreCase);

    public ModuleRunService(GrcsHttpClient grcs, WcsSettingsService settings, TaskTemplateStore templates,
        RcsProtocolTemplateStore protocols, ITaskStageService stages, ILogger<ModuleRunService> logger,
        AutomationLogService logs, MockRuleStore mocks, TaskLifecycleService lifecycle,
        ModuleEffectService effects, ModuleExecutionLogStore moduleLogs)
    {
        _grcs = grcs;
        _settings = settings;
        _templates = templates;
        _protocols = protocols;
        _stages = stages;
        _logger = logger;
        _logs = logs;
        _mocks = mocks;
        _lifecycle = lifecycle;
        _effects = effects;
        _moduleLogs = moduleLogs;
    }

    public class ModuleCtx
    {
        public string Start = "";
        public string End = "";
        public string Container = "";
        public string Pallet = "";
        public string Cargo = "";
        public string Warehouse = "";
        public string TaskType = "";
        public string TaskId = "";
        private readonly Dictionary<string, string> _context = new(StringComparer.OrdinalIgnoreCase);

        public string GetContext(string key)
            => _context.TryGetValue(key, out var value) ? value : "";

        public void SetContext(string key, string? value)
            => _context[key] = value?.Trim() ?? "";

        public IReadOnlyDictionary<string, string> SnapshotContext()
            => new Dictionary<string, string>(_context, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(bool ok, int code, string json)> SendTaskWithModulesAsync(WcsTaskGroup group, string? roundId = null)
    {
        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            return (false, 0, "GRCS address is not configured.");
        if (!_mocks.HasTaskStageRule())
            return (false, 0, "A task stage mock rule is required before dispatch.");
        var task = group.Tasks.FirstOrDefault();
        if (task == null) return (false, 0, "The task group is empty.");

        var template = _templates.GetAll().FirstOrDefault(t =>
            string.Equals(t.Value, task.TaskType, StringComparison.OrdinalIgnoreCase));
        var ctx = BuildCtxFromGroup(group, task);
        _stages.RecordCreated([new TaskLedgerEntry
        {
            TaskId = task.TaskId,
            TaskType = task.TaskType,
            ContainerCode = !string.IsNullOrWhiteSpace(task.PalletCode) ? task.PalletCode
                : task.ContainerCode.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ? "" : task.ContainerCode,
            CargoCode = !string.IsNullOrWhiteSpace(task.CargoCode) ? task.CargoCode
                : task.ContainerCode.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ? task.ContainerCode : "",
            StartStationCode = task.StationCode.FirstOrDefault() ?? "",
            EndStationCode = task.StationCode.LastOrDefault() ?? "",
            Warehouse = group.Warehouse,
            Time = DateTime.Now.ToString("O"),
            Ok = false,
            StatusCode = 0,
        }]);

        if (template?.WorkflowSteps is { Count: > 0 })
            return await SendTaskWithWorkflowAsync(group, task, template, ctx, roundId);

        if (template != null && !await RunModulesAsync("BEFORE_MODULE", template.Start?.BeforeModules ?? [], ctx, roundId, true))
        {
            _stages.TryRecordSystemEvent(task.TaskId, "CANCELLED", false, -1);
            return (false, -1, "A before-start module failed; task was not dispatched.");
        }

        if (roundId != null)
            _logs.Add(roundId, "Dispatch payload (active RCS carry template): " + JsonSerializer.Serialize(group), "#93c5fd");
        _lifecycle.BeginDispatch(task.TaskId);
        var (ok, code, json) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, group);
        var accepted = ok;
        if (!accepted)
        {
            _lifecycle.DispatchFailed(task.TaskId, code == 0);
            _stages.RecordDispatchResult(task.TaskId, false, code);
            _stages.TryRecordSystemEvent(task.TaskId, "CANCELLED", false, code);
            return (false, code, json);
        }

        _stages.RecordDispatchResult(task.TaskId, true, code);
        if (roundId != null) _logs.Add(roundId, $"Active RCS accepted task ({task.TaskId}); waiting 2 seconds.", "#4ade80");
        if (template != null)
        {
            await Task.Delay(TestAfterDispatchDelayMs);
            await RunModulesAsync("AFTER_START_MODULE", template.Start?.AfterModules ?? [], ctx, roundId);
        }
        return (true, code, json);
    }

    public async Task<bool> RunEndModulesAsync(string taskId, ModuleCtx? preset = null, string? roundId = null)
    {
        if (_workflowCompletions.TryGetValue(taskId, out var workflow))
        {
            try { return await workflow.Task; }
            finally { _workflowCompletions.TryRemove(taskId, out _); }
        }

        var ctx = preset ?? BuildCtxFromRecord(taskId);
        if (ctx == null) return false;
        var template = _templates.GetAll().FirstOrDefault(t =>
            string.Equals(t.Value, ctx.TaskType, StringComparison.OrdinalIgnoreCase));
        return template == null
            || await RunModulesAsync("AFTER_END_MODULE", template.End?.AfterModules ?? [], ctx, roundId, true);
    }

    private async Task<(bool ok, int code, string json)> SendTaskWithWorkflowAsync(
        WcsTaskGroup group, WcsTaskItem task, TaskTemplateDto template, ModuleCtx ctx, string? roundId)
    {
        var steps = template.WorkflowSteps.Where(step => step.Enabled).ToList();
        var dispatchIndexes = steps.Select((step, index) => (step, index))
            .Where(item => string.Equals(item.step.Kind, TaskWorkflowStepKinds.Dispatch, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index).ToList();
        if (dispatchIndexes.Count != 1)
            return (false, 400, "任务流程必须且只能包含一个启用的 RCS 下发节点。");

        var dispatchIndex = dispatchIndexes[0];
        for (var i = 0; i < dispatchIndex; i++)
        {
            var step = steps[i];
            if (!string.Equals(step.Kind, TaskWorkflowStepKinds.BusinessTemplate, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(step.TemplateId))
                return (false, 400, "RCS 下发之前只能配置业务模板节点。");
        }

        if (roundId != null)
            _logs.Add(roundId, "开始执行任务流程模板：" + template.Label, "#93c5fd");
        foreach (var step in steps.Take(dispatchIndex))
        {
            if (!await RunModulesAsync("WORKFLOW_TEMPLATE", [step.TemplateId], ctx, roundId, true))
            {
                _stages.TryRecordSystemEvent(task.TaskId, $"WORKFLOW_STEP_FAILED:{step.Id}", false, 0);
                _stages.TryRecordSystemEvent(task.TaskId, "WORKFLOW_FAILED", false, 0);
                _lifecycle.Cancel(task.TaskId);
                return (false, 424, $"下发 RCS 前的业务模板执行失败：{step.TemplateId}");
            }
        }

        var settings = _settings.Get();
        if (settings == null || string.IsNullOrWhiteSpace(settings.GrcsBaseUrl))
            return (false, 0, "RCS address is not configured.");
        if (roundId != null)
            _logs.Add(roundId, "下发任务到当前 RCS，并等待接口成功响应。", "#93c5fd");
        _lifecycle.BeginDispatch(task.TaskId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workflowCompletions[task.TaskId] = completion;
        var (ok, code, json) = await _grcs.SendTaskGroupAsync(settings.GrcsBaseUrl, group);
        if (!ok)
        {
            _workflowCompletions.TryRemove(task.TaskId, out _);
            completion.TrySetResult(false);
            _lifecycle.DispatchFailed(task.TaskId, code == 0);
            _stages.RecordDispatchResult(task.TaskId, false, code);
            _stages.TryRecordSystemEvent(task.TaskId, "CANCELLED", false, code);
            return (false, code, json);
        }

        _stages.RecordDispatchResult(task.TaskId, true, code);
        _ = ContinueWorkflowAsync(task.TaskId, ctx, steps.Skip(dispatchIndex + 1).ToList(), roundId, completion);
        return (true, code, json);
    }

    private async Task ContinueWorkflowAsync(string taskId, ModuleCtx ctx, List<TaskWorkflowStepDto> steps,
        string? roundId, TaskCompletionSource<bool> completion)
    {
        try
        {
            foreach (var step in steps)
            {
                if (string.Equals(step.Kind, TaskWorkflowStepKinds.WaitRcsStage, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(step.StageCode))
                        throw new InvalidOperationException($"流程节点 {step.Id} 未配置 RCS 阶段。");
                    var timeout = TimeSpan.FromSeconds(Math.Clamp(step.TimeoutSeconds, 1, 86400));
                    if (roundId != null)
                        _logs.Add(roundId, $"等待 RCS 上报阶段 {step.StageCode}（最多 {timeout.TotalSeconds:0} 秒）。", "#93c5fd");
                    await _stages.WaitForStageAsync(taskId, step.StageCode, timeout);
                    _stages.TryRecordSystemEvent(taskId, $"WORKFLOW_STAGE:{step.StageCode}", true, 200);
                    continue;
                }

                if (!string.Equals(step.Kind, TaskWorkflowStepKinds.BusinessTemplate, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(step.TemplateId))
                    throw new InvalidOperationException($"流程节点 {step.Id} 类型或模板配置无效。");
                if (!await RunModulesAsync("WORKFLOW_TEMPLATE", [step.TemplateId], ctx, roundId, true))
                    throw new InvalidOperationException($"业务模板执行失败：{step.TemplateId}");
            }

            _stages.TryRecordSystemEvent(taskId, "WORKFLOW_COMPLETED", true, 200);
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务流程执行失败：{TaskId}", taskId);
            _stages.TryRecordSystemEvent(taskId, "WORKFLOW_FAILED", false, 0);
            if (roundId != null) _logs.Add(roundId, $"任务流程失败：{ex.Message}", "#f87171");
            completion.TrySetResult(false);
        }
    }

    private async Task<bool> RunModulesAsync(string prefix, List<string> ids, ModuleCtx ctx,
        string? roundId = null, bool stopOnFailure = false)
    {
        var allOk = true;
        foreach (var id in ids)
        {
            var startedAt = DateTime.Now.ToString("O");
            var module = _protocols.GetActive().FeatureModules.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (module == null || string.IsNullOrWhiteSpace(module.ApiUrl))
            {
                _stages.TryRecordSystemEvent(ctx.TaskId, $"{prefix}:{id}", false, 0);
                RecordModuleLog(ctx, prefix, id, module?.Name ?? id, false, 0, startedAt,
                    BuildModuleDetail(ctx, "Module configuration is missing or API URL is empty.", null,
                        null, null, null, null, null));
                allOk = false;
                if (stopOnFailure) return false;
                continue;
            }

            if (!_effects.Validate(module, out var validationMessage))
            {
                _stages.TryRecordSystemEvent(ctx.TaskId, $"EFFECT_CONFIGURATION:{id}", false, 0);
                RecordModuleLog(ctx, prefix, id, module.Name, false, 0, startedAt,
                    BuildModuleDetail(ctx, validationMessage, module.PreExecutionEffect, false,
                        null, null, null, null));
                allOk = false;
                if (roundId != null) _logs.Add(roundId, validationMessage, "#f87171");
                if (stopOnFailure) return false;
                continue;
            }

            var preOk = true;
            string? preException = null;
            try { preOk = _effects.HandleBefore(ctx, module.PreExecutionEffect); }
            catch (Exception ex) { preOk = false; preException = ex.Message; }
            if (!preOk)
            {
                _stages.TryRecordSystemEvent(ctx.TaskId, $"PRE_EFFECT:{id}", false, 0);
                RecordModuleLog(ctx, prefix, id, module.Name, false, 0, startedAt,
                    BuildModuleDetail(ctx, preException ?? "Pre-execution effect failed.", module.PreExecutionEffect, false,
                        null, null, null, null));
                allOk = false;
                if (roundId != null)
                    _logs.Add(roundId, $"Module '{module.Name}' pre-execution effect failed.", "#f87171");
                if (stopOnFailure) return false;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(module.PreExecutionEffect))
                _stages.TryRecordSystemEvent(ctx.TaskId, $"PRE_EFFECT:{id}", true, 200);

            var alreadyRegistered = string.Equals(module.PreExecutionEffect?.Trim(), ModulePreExecutionEffects.PrepareReturnTask,
                                        StringComparison.OrdinalIgnoreCase)
                && string.Equals(ctx.GetContext("returnTaskRegistered"), "true", StringComparison.OrdinalIgnoreCase);
            var body = new Dictionary<string, object?>();
            foreach (var parameter in module.Params) body[parameter.Name] = Resolve(parameter, ctx);
            var baseUrl = _protocols.ResolveBaseUrl(_settings.Get()?.GrcsBaseUrl);
            var url = baseUrl.TrimEnd('/') + module.ApiUrl;
            var method = ResolveHttpMethod(module.HttpMethod);
            string? requestBody = null;
            if (roundId != null) _logs.Add(roundId, $"Run module '{module.Name}' {method.Method} {url}", "#93c5fd");

            var ok = false;
            var code = 0;
            var json = "";
            string? requestException = null;
            try
            {
                requestBody = BuildModuleRequestBody(module, ctx, body);
                (ok, code, json) = alreadyRegistered
                    ? (true, 200, "{\"success\":true,\"message\":\"Return task was already registered.\"}")
                    : await _grcs.ForwardAsync(url, method, requestBody, module.TimeoutSeconds);
                if (ok) ok = IsConfiguredSuccess(module, json);
            }
            catch (Exception ex)
            {
                requestException = ex.Message;
                json = ex.ToString();
            }
            _stages.TryRecordSystemEvent(ctx.TaskId, $"{prefix}:{id}", ok, code);

            var effectOk = true;
            string? effectException = null;
            if (ok)
            {
                try { effectOk = _effects.HandleSucceeded(ctx, module.InventoryEffect, prefix); }
                catch (Exception ex) { effectOk = false; effectException = ex.Message; }
                if (!effectOk)
                {
                    allOk = false;
                    _logger.LogError("Module {Module} succeeded, but inventory effect {Effect} failed for {TaskId}.",
                        module.Name, module.InventoryEffect, ctx.TaskId);
                    if (roundId != null)
                        _logs.Add(roundId, $"Module '{module.Name}' succeeded, but inventory effect '{module.InventoryEffect}' failed.", "#f87171");
                }
            }

            if (!ok)
            {
                try { _effects.HandleFailed(ctx, module.PreExecutionEffect, code); }
                catch (Exception ex) { effectException ??= ex.Message; }
                allOk = false;
            }

            var completed = ok && effectOk;
            var summary = requestException ?? effectException
                ?? (completed ? "Module execution completed." : "Module execution failed.");
            RecordModuleLog(ctx, prefix, id, module.Name, completed, code, startedAt,
                BuildModuleDetail(ctx, summary, module.PreExecutionEffect, true,
                    url, ParseJsonOrString(requestBody), new { httpStatus = code, body = json, exception = requestException, skipped = alreadyRegistered },
                    new { effect = module.InventoryEffect, ok = effectOk, exception = effectException }, method.Method));

            if (roundId != null)
                _logs.Add(roundId, $"{(completed ? "OK" : "FAILED")} module '{module.Name}' HTTP {code}: {json[..Math.Min(json.Length, 200)]}", completed ? "#4ade80" : "#f87171");
            _logger.LogInformation("[Module] {TaskId} {Prefix} {Module}: {Ok} {Code}", ctx.TaskId, prefix, module.Name, completed, code);
            if (!completed && stopOnFailure) return false;
        }
        return allOk;
    }

    private void RecordModuleLog(ModuleCtx ctx, string phase, string moduleId, string moduleName,
        bool success, int httpStatus, string startedAt, object detail)
    {
        _moduleLogs.Record(new ModuleExecLogEntry
        {
            TaskId = ctx.TaskId,
            ModuleId = moduleId,
            Module = moduleName,
            ExecutionPhase = phase,
            Status = success ? "success" : "fail",
            HttpCode = httpStatus,
            StartedAt = startedAt,
            FinishedAt = DateTime.Now.ToString("O"),
            DetailJson = JsonSerializer.Serialize(detail)
        });
    }

    private static object BuildModuleDetail(ModuleCtx ctx, string summary, string? preEffect, bool? preOk,
        string? url, object? body, object? response, object? after, string method = "POST")
        => new
        {
            summary,
            context = new
            {
                taskId = ctx.TaskId,
                taskType = ctx.TaskType,
                warehouse = ctx.Warehouse,
                start = ctx.Start,
                end = ctx.End,
                container = ctx.Container,
                pallet = ctx.Pallet,
                cargo = ctx.Cargo,
                values = ctx.SnapshotContext()
            },
            before = new { effect = preEffect ?? "", ok = preOk, values = ctx.SnapshotContext() },
            request = url == null ? null : new { method, url, body },
            response,
            after
        };

    private static ModuleCtx BuildCtxFromGroup(WcsTaskGroup group, WcsTaskItem task) => new()
    {
        Start = task.StationCode.FirstOrDefault() ?? "",
        End = task.StationCode.LastOrDefault() ?? "",
        Container = task.ContainerCode,
        Pallet = !string.IsNullOrWhiteSpace(task.PalletCode) ? task.PalletCode
            : task.ContainerCode.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ? "" : task.ContainerCode,
        Cargo = !string.IsNullOrWhiteSpace(task.CargoCode) ? task.CargoCode
            : task.ContainerCode.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ? task.ContainerCode : "",
        Warehouse = group.Warehouse,
        TaskType = task.TaskType,
        TaskId = task.TaskId,
    };

    private ModuleCtx? BuildCtxFromRecord(string taskId)
    {
        var record = _stages.GetAll().FirstOrDefault(r => r.IsCreated && string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        return record == null ? null : new ModuleCtx
        {
            Start = record.StartStationCode,
            End = record.EndStationCode,
            // task_records 将托盘与货物拆列保存；纯货任务没有托盘时，任务容器回退为货物号。
            Container = !string.IsNullOrWhiteSpace(record.ContainerCode) ? record.ContainerCode : record.CargoCode,
            Pallet = record.ContainerCode,
            Cargo = record.CargoCode,
            Warehouse = record.Warehouse,
            TaskType = record.TaskType,
            TaskId = record.TaskId,
        };
    }

    private static object? Resolve(WorkParamDto parameter, ModuleCtx ctx) => parameter.Source switch
    {
        WorkValueSourceDto.StartPoint => ctx.Start,
        WorkValueSourceDto.EndPoint => ctx.End,
        WorkValueSourceDto.TaskContainer => ctx.Container,
        WorkValueSourceDto.TaskPallet => ctx.Pallet,
        WorkValueSourceDto.TaskCargo => ctx.Cargo,
        WorkValueSourceDto.TaskWarehouse => ctx.Warehouse,
        WorkValueSourceDto.TaskType => ctx.TaskType,
        WorkValueSourceDto.TaskId => ctx.TaskId,
        WorkValueSourceDto.Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        WorkValueSourceDto.ModuleContext => ctx.GetContext(parameter.FixedValue),
        WorkValueSourceDto.JsonLiteral => ResolveJsonLiteral(parameter.FixedValue),
        _ => parameter.FixedValue,
    };

    private static object? ResolveJsonLiteral(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch
        {
            return value;
        }
    }

    private static object? ParseJsonOrString(string? value)
    {
        if (value == null) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch { return value; }
    }

    private static HttpMethod ResolveHttpMethod(string? method)
    {
        var value = method?.Trim().ToUpperInvariant();
        return value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE"
            ? new HttpMethod(value)
            : HttpMethod.Post;
    }

    private static string? BuildModuleRequestBody(FeatureModuleDto module, ModuleCtx ctx, Dictionary<string, object?> parameters)
    {
        if (module.HttpMethod?.Trim().Equals("GET", StringComparison.OrdinalIgnoreCase) == true)
            return null;

        if (string.IsNullOrWhiteSpace(module.RequestBodyTemplate))
            return JsonSerializer.Serialize(parameters);

        var payloadJson = JsonSerializer.Serialize(parameters);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PayloadJson"] = payloadJson,
            ["TaskId"] = JsonSerializer.Serialize(ctx.TaskId),
            ["TaskType"] = JsonSerializer.Serialize(ctx.TaskType),
            ["Warehouse"] = JsonSerializer.Serialize(ctx.Warehouse),
            ["Start"] = JsonSerializer.Serialize(ctx.Start),
            ["End"] = JsonSerializer.Serialize(ctx.End),
            ["Container"] = JsonSerializer.Serialize(ctx.Container),
            ["Pallet"] = JsonSerializer.Serialize(ctx.Pallet),
            ["Cargo"] = JsonSerializer.Serialize(ctx.Cargo),
        };
        foreach (var (key, value) in parameters)
            values[key] = JsonSerializer.Serialize(value);

        var body = module.RequestBodyTemplate;
        foreach (var (key, value) in values)
            body = body.Replace("{{" + key + "}}", value, StringComparison.OrdinalIgnoreCase);
        if (body.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException("业务模板请求体包含未知或未提供的占位符。");
        using var _ = JsonDocument.Parse(body);
        return body;
    }

    private static bool IsConfiguredSuccess(FeatureModuleDto module, string responseBody)
    {
        if (string.IsNullOrWhiteSpace(module.SuccessProperty)) return true;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var current = document.RootElement;
            foreach (var segment in module.SuccessProperty.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (current.ValueKind != JsonValueKind.Object)
                    return false;
                var property = current.EnumerateObject().FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (property.Name == null) return false;
                current = property.Value;
            }

            var actual = current.ValueKind == JsonValueKind.String
                ? current.GetString()
                : current.GetRawText();
            return string.Equals(actual, module.SuccessValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

}
