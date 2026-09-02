using GrcsBackend.Modules.Shared.Infrastructure.Repository;
using GrcsBackend.Contracts.Entities;
using GrcsBackend.Contracts.Dtos;
using GrcsBackend.Modules.Wcs.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// 模块执行记录（内存环形缓冲 + SQLite 持久化，上限 500 条，自增 Id + sinceId 增量拉取）。
/// 取代前端 ModuleRunnerService 的本地日志：所有任务（手动 + 自动化）的模块执行统一在后端发生，
/// 前端「模块执行记录」面板实时接收 SignalR 推送（新增单条 ModuleExecLogAdded，
/// 清空已处理后广播全量 ModuleExecLogsReset，连接建立回放 ModuleExecLogsReset），不再轮询。
/// 重启后从 DB 恢复最近 500 条。
/// </summary>
public class ModuleExecLogStore
{
    private readonly object _lock = new();
    private readonly List<ModuleExecLogEntry> _logs = [];
    private long _nextId = 1;
    private const int Max = 500;
    private readonly IUnitOfWorkFactory _uow;
    private readonly IHubContext<TaskStageRealtimeHub> _hub;

    public ModuleExecLogStore(IUnitOfWorkFactory uowFactory, IHubContext<TaskStageRealtimeHub> hub)
    {
        _uow = uowFactory;
        _hub = hub;
        try
        {
            using var uow = _uow.Create();
            var rows = uow.Repository<ModuleExecLogRow>().Query()
                .OrderByDescending(r => r.Id).Take(Max).ToList();
            foreach (var r in rows)
            {
                _logs.Insert(0, new ModuleExecLogEntry
                {
                    Id = r.Id,
                    Time = DateTime.TryParse(r.CreatedAt, out var t) ? t.ToString("HH:mm:ss") : r.CreatedAt,
                    TaskId = r.TaskId,
                    Point = r.Point,
                    Module = r.Module,
                    Ok = r.Ok,
                    HttpCode = r.HttpCode,
                    Detail = r.Detail,
                });
                if (r.Id >= _nextId) _nextId = r.Id + 1;
            }
        }
        catch { }
    }

    public long Add(string taskId, string point, string module, bool ok, int httpCode, string detail)
    {
        lock (_lock)
        {
            var detailTrim = detail.Length > 400 ? detail[..400] : detail;
            long dbId;
            using (var uow = _uow.Create())
            {
                var repo = uow.Repository<ModuleExecLogRow>();
                var row = new ModuleExecLogRow
                {
                    TaskId = taskId,
                    Point = point,
                    Module = module,
                    Ok = ok,
                    HttpCode = httpCode,
                    Detail = detailTrim,
                    CreatedAt = DateTime.Now.ToString("O"),
                };
                repo.AddAsync(row).GetAwaiter().GetResult();
                uow.CommitAsync().GetAwaiter().GetResult();
                dbId = row.Id;
            }
            var entry = new ModuleExecLogEntry
            {
                Id = dbId,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                TaskId = taskId,
                Point = point,
                Module = module,
                Ok = ok,
                HttpCode = httpCode,
                Detail = detailTrim,
            };
            _logs.Add(entry);
            if (_logs.Count > Max)
            {
                _logs.RemoveRange(0, _logs.Count - Max);
                TrimDb(Max);
            }
            // 增量广播单条（前端按 Id 判重后插头部，无重复）
            _ = _hub.Clients.All.SendAsync("ModuleExecLogAdded", entry);
            return dbId;
        }
    }

    public List<ModuleExecLogEntry> GetSince(long sinceId)
    {
        lock (_lock) return _logs.Where(l => l.Id > sinceId).ToList();
    }

    public ModuleExecLogEntry? GetById(long id)
    {
        lock (_lock) return _logs.FirstOrDefault(l => l.Id == id);
    }

    /// <summary>清空已处理：只删除成功（HTTP 2xx）记录，失败/异常记录保留；广播剩余全量快照供前端整表替换。</summary>
    public void ClearProcessed()
    {
        lock (_lock)
        {
            _logs.RemoveAll(l => l.Ok);
            using var uow = _uow.Create();
            uow.Repository<ModuleExecLogRow>().DeleteWhereAsync(r => r.Ok).GetAwaiter().GetResult();
            uow.CommitAsync().GetAwaiter().GetResult();
        }
        _ = _hub.Clients.All.SendAsync("ModuleExecLogsReset", new { maxId = MaxId, entries = GetSince(0) });
    }

    /// <summary>DB 裁剪：保留最近 max 条。</summary>
    private void TrimDb(int max)
    {
        using var uow = _uow.Create();
        var repo = uow.Repository<ModuleExecLogRow>();
        var keep = repo.Query().OrderByDescending(r => r.Id).Take(max).Select(r => r.Id).ToList();
        if (keep.Count > 0)
            repo.DeleteWhereAsync(r => !keep.Contains(r.Id)).GetAwaiter().GetResult();
        uow.CommitAsync().GetAwaiter().GetResult();
    }

    public long MaxId { get { lock (_lock) { return _nextId - 1; } } }
}