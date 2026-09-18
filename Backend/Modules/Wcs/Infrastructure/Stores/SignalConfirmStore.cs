using System.Text.Json;
using Contracts;
using Backend.Shared.Infrastructure.Repository;
using Contracts.Dtos;
using Contracts.Entities;
using Entities = Contracts.Entities;
using WCSBackend.Modules.Wcs.Console.Services;
using Mapster;

namespace WCSBackend.Modules.Wcs.Infrastructure;
public class SignalConfirmStore
{
    private readonly ITaskStageService _stages;

    public SignalConfirmStore(ITaskStageService stages) => _stages = stages;

    public bool Set(string kind, string taskId, string? value)
    {
        return _stages.TryRecordSystemEvent(taskId, SignalStage(kind), true);
    }

    public void Remove(string kind, string taskId)
    {
        // 任务记录是审计历史，不删除已发送的信号。该方法仅保留兼容调用。
    }

    /// <summary>全部确认状态按 kind 分组返回。</summary>
    public Dictionary<string, List<WorkflowStateRow>> GetAll()
    {
        return _stages.GetAll()
            .Where(x => x.Stage.StartsWith("SIGNAL_", StringComparison.OrdinalIgnoreCase))
            .Select(x => new WorkflowStateRow
            {
                Kind = x.Stage["SIGNAL_".Length..].ToLowerInvariant(),
                TaskId = x.TaskId,
                Time = x.Time.ToString("O"),
            })
            .GroupBy(x => x.Kind)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private static string SignalStage(string kind) => "SIGNAL_" + kind.Trim().ToUpperInvariant();
}

/// <summary>异常记录台账（SQLite exception_records 表，纯 HTTP 读写）。Singleton。</summary>
