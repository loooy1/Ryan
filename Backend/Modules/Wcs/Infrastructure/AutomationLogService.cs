using GrcsBackend.Contracts.Dtos;
using System.Collections.Generic;

namespace GrcsBackend.Modules.Wcs.Infrastructure;

/// <summary>
/// 自动化/批量/信号执行日志（按「轮次」分组，内存存储，进程重启清空）。
/// 每轮下发(BeginRound)生成一个标题；该轮所有日志写入对应分组；任务全部完成后 ClearRound 清除标题与日志。
/// 非轮次日志（信号自动等）落入固定「系统 / 信号」分组。前端按标题折叠展示，点击展开查看该轮日志。
/// </summary>
public class AutomationLogService
{
    private readonly object _lock = new();
    private readonly List<LogRoundDto> _rounds = new();
    private long _nextId = 1;
    private const int MaxRounds = 100;
    private const int MaxSysEntries = 500;
    private const string SysRoundId = "__sys__";

    private LogRoundDto EnsureSysRound()
    {
        var r = _rounds.FirstOrDefault(x => x.RoundId == SysRoundId);
        if (r == null)
        {
            r = new LogRoundDto { RoundId = SysRoundId, Title = "系统日志", StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            _rounds.Add(r);
        }
        return r;
    }

    /// <summary>开始一轮，返回轮次 Id（作为后续 Add 的分组键）。parentRoundId 非空时挂为二级子日志（多选时每个模板一个）。</summary>
    public string BeginRound(string title, string? parentRoundId = null)
    {
        string id;
        lock (_lock)
        {
            id = Guid.NewGuid().ToString("N")[..8];
            _rounds.Add(new LogRoundDto { RoundId = id, ParentRoundId = parentRoundId ?? "", Title = title, StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        }
        Trim();
        return id;
    }

    /// <summary>按「父轮次」裁剪：保留最近 MaxRounds 个非系统父轮（含其所有子日志），系统/信号组永不被裁，避免孤儿子日志。</summary>
    private void Trim()
    {
        lock (_lock)
        {
            var parents = _rounds.Where(r => r.ParentRoundId == "" && r.RoundId != SysRoundId).ToList();
            while (parents.Count > MaxRounds)
            {
                var oldest = parents[0];
                _rounds.RemoveAll(r => r.RoundId == oldest.RoundId || r.ParentRoundId == oldest.RoundId);
                parents = _rounds.Where(r => r.ParentRoundId == "" && r.RoundId != SysRoundId).ToList();
            }
        }
    }

    /// <summary>向指定轮次追加一条日志（轮次不存在则忽略）。</summary>
    public void Add(string roundId, string message, string color = "#94a3b8")
    {
        lock (_lock)
        {
            var r = _rounds.FirstOrDefault(x => x.RoundId == roundId);
            if (r == null) return;
            r.Entries.Add(new LogEntryDto { Id = _nextId++, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Message = message, Color = color });
        }
    }

    /// <summary>非轮次日志（信号自动等）落入固定「系统 / 信号」分组。</summary>
    public void Add(string message, string color = "#94a3b8")
    {
        lock (_lock)
        {
            EnsureSysRound().Entries.Add(new LogEntryDto { Id = _nextId++, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Message = message, Color = color });
        }
    }

    /// <summary>更新式追加：系统通知中同 key（消息前缀）只保留最新一条并刷新时间，避免反复刷屏；调用方可看到最后更新时间。</summary>
    public void AddOrUpdate(string key, string message, string color = "#94a3b8")
    {
        lock (_lock)
        {
            var r = EnsureSysRound();
            var existing = r.Entries.LastOrDefault(e => e.Message.StartsWith(key));
            if (existing != null)
            {
                existing.Message = message;
                existing.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                existing.Color = color;
            }
            else
            {
                r.Entries.Add(new LogEntryDto { Id = _nextId++, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Message = message, Color = color });
                // 系统日志环形缓冲：超出上限时丢弃最旧条目，避免长期运行 OOM
                while (r.Entries.Count > MaxSysEntries) r.Entries.RemoveAt(0);
            }
        }
    }

    /// <summary>标记轮次完成（前端可据状态提示）。</summary>
    public void CompleteRound(string roundId)
    {
        lock (_lock) { var r = _rounds.FirstOrDefault(x => x.RoundId == roundId); if (r != null) r.Completed = true; }
    }

    /// <summary>修改轮次标题（如选托盘成功后由「第 N 轮」回写为成功轮数）。</summary>
    public void RenameRound(string roundId, string title)
    {
        lock (_lock) { var r = _rounds.FirstOrDefault(x => x.RoundId == roundId); if (r != null) r.Title = title; }
    }

    /// <summary>清除指定轮次（含其所有子轮次）。任务完成后调用。</summary>
    public void ClearRound(string roundId)
    {
        if (roundId == SysRoundId) return;
        lock (_lock) { _rounds.RemoveAll(x => x.RoundId == roundId || x.ParentRoundId == roundId); }
    }

    /// <summary>返回所有轮次（含条目）供前端渲染。</summary>
    public List<LogRoundDto> GetRounds()
    {
        lock (_lock) { return _rounds.Select(r => new LogRoundDto { RoundId = r.RoundId, ParentRoundId = r.ParentRoundId, Title = r.Title, StartTime = r.StartTime, Completed = r.Completed, Entries = r.Entries.ToList() }).ToList(); }
    }

    public void Clear() { lock (_lock) { _rounds.Clear(); } }

    /// <summary>仅清空系统通知的条目（保留标题分组）。</summary>
    public void ClearSystem() { lock (_lock) { var r = _rounds.FirstOrDefault(x => x.RoundId == SysRoundId); if (r != null) r.Entries.Clear(); } }
}
