using Microsoft.Extensions.Hosting;
using GrcsBackend.Modules.Wcs.Console.Services;
using GrcsBackend.Modules.Wcs.Infrastructure;

namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>
/// 终点模块后台执行器：订阅 ITaskStageService.TaskFinished，
/// 对任意「非自动化（Auto_ 前缀）」任务在 FINISHED 后执行其模板的终点(End)模块。
/// 自动化任务的终点模块由 AutoTemplateRunner 自行执行（需 await 其完成以闸门下一步），此处跳过避免重复。
/// </summary>
public class FinishedModuleWatcher : IHostedService
{
    private readonly ITaskStageService _stage;
    private readonly ModuleRunService _modules;

    public FinishedModuleWatcher(ITaskStageService stage, ModuleRunService modules)
    {
        _stage = stage;
        _modules = modules;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _stage.TaskFinished += OnFinished;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stage.TaskFinished -= OnFinished;
        return Task.CompletedTask;
    }

    private void OnFinished(string taskId)
    {
        if (string.IsNullOrEmpty(taskId)) return;
        // 自动化任务（Auto_ 前缀）的终点模块由 AutoTemplateRunner 自己跑（需 await 完成），此处跳过
        if (taskId.StartsWith("Auto_", StringComparison.OrdinalIgnoreCase)) return;
        _ = _modules.RunEndModulesAsync(taskId);
    }
}
