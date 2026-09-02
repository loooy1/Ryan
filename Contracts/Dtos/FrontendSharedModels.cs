namespace GrcsBackend.Contracts.Dtos;

/// <summary>自动化/手动运行总览快照（GET /api/wcs/auto/status）。</summary>
public class AutoStatusSnapshot
{
    public bool Running { get; set; }
    public string AutoTabId { get; set; } = "";
    public int Interval { get; set; }
    public string ActiveTemplateId { get; set; } = "";
    public string ActiveTemplateName { get; set; } = "";
    public int Executed { get; set; }
    public string Status { get; set; } = "";
    public bool MoveRunning { get; set; }
    public string MoveTabId { get; set; } = "";
    public int MoveTotal { get; set; }
    public int MoveOk { get; set; }
    public int MoveFail { get; set; }
    public string MoveLastError { get; set; } = "";
    public bool NestRunning { get; set; }
    public List<AutoTemplateDto> Templates { get; set; } = [];
    public WcsSettingsDto Settings { get; set; } = new();
    public SignalFlagsDto Signals { get; set; } = new();
}

/// <summary>信号自动化开关（到达/取走/自动下发）。</summary>
public class SignalFlagsDto
{
    public bool ArrivalAuto { get; set; }
    public bool RemovalAuto { get; set; }
    public bool AutoSend { get; set; }
}

/// <summary>移动任务循环租约登记结果（POST /api/wcs/auto/move/start）。</summary>
public class MoveLeaseResult
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
}

/// <summary>纯移动任务循环状态（SignalR「MoveTaskStats」广播 + GET status 轮询字段）。</summary>
public class MoveTaskStatsDto
{
    public bool Running { get; set; }
    public string TabId { get; set; } = "";
    public int Interval { get; set; }
    public int Seq { get; set; }
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Fail { get; set; }
    public string LastError { get; set; } = "";
    public string LastStation { get; set; } = "";
}

/// <summary>归巢执行结果（POST /api/wcs/auto/nest/run）。</summary>
public class NestRunResult
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
}

/// <summary>准入状态（GET /api/wcs/status：自动模式 + 待确认数）。</summary>
public class AdmittanceStatusDto
{
    public bool AutoMode { get; set; }
    public int PendingCount { get; set; }
}

/// <summary>WCS 代理响应（/api/wcs/grcs/* 统一返回 { ok, code, json }）。</summary>
public class GrcsProxyResult
{
    public bool Ok { get; set; }
    public int Code { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>地图缓存响应（GET /api/wcs/map）。</summary>
public class MapCacheDto
{
    public string SavedAt { get; set; } = "";
    public int PathsCount { get; set; }
    public List<MapStationLite> Stations { get; set; } = [];
}

/// <summary>地图上传负载（POST /api/wcs/map/upload）。</summary>
public class MapUploadPayload
{
    public string SavedAt { get; set; } = "";
    public int PathsCount { get; set; }
    public List<MapStationLite> Stations { get; set; } = [];
}

/// <summary>分拣已发送的编辑参数（workflow_state sent 行的 value JSON）。</summary>
public class SortingSendParams
{
    public string ReturnTaskId { get; set; } = "";
    public bool RemoveContainer { get; set; }
    public string DestStation { get; set; } = "";
    public string DestArea { get; set; } = "";
}

/// <summary>模块执行记录条目（GET /api/wcs/modules/logs）。</summary>
public class ModuleExecLogEntry
{
    public long Id { get; set; }
    public string Time { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Point { get; set; } = "";
    public string Module { get; set; } = "";
    public bool Ok { get; set; }
    public int HttpCode { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>模块执行记录增量响应（GET /api/wcs/modules/logs）。</summary>
public class ModuleExecLogsResponse
{
    public long MaxId { get; set; }
    public List<ModuleExecLogEntry> Entries { get; set; } = [];
}