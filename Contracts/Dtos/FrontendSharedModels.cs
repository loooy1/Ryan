namespace Contracts.Dtos;

/// <summary>鑷姩鍖?鎵嬪姩杩愯鎬昏蹇収锛圙ET /api/wcs/auto/status锛夈€?/summary>
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
}

/// <summary>淇″彿鑷姩鍖栧紑鍏筹紙鍒拌揪/鍙栬蛋/鑷姩涓嬪彂锛夈€?/summary>
/// <summary>绉诲姩浠诲姟寰幆绉熺害鐧昏缁撴灉锛圥OST /api/wcs/auto/move/start锛夈€?/summary>
public class MoveLeaseResult
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
}

/// <summary>绾Щ鍔ㄤ换鍔″惊鐜姸鎬侊紙SignalR銆孧oveTaskStats銆嶅箍鎾?+ GET status 杞瀛楁锛夈€?/summary>
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

/// <summary>褰掑发鎵ц缁撴灉锛圥OST /api/wcs/auto/nest/run锛夈€?/summary>
public class NestRunResult
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
}

/// <summary>鍑嗗叆鐘舵€侊紙GET /api/wcs/status锛氳嚜鍔ㄦā寮?+ 寰呯‘璁ゆ暟锛夈€?/summary>
public class AdmittanceStatusDto
{
    public bool AutoMode { get; set; }
    public int PendingCount { get; set; }
}

/// <summary>WCS 浠ｇ悊鍝嶅簲锛?api/wcs/grcs/* 缁熶竴杩斿洖 { ok, code, json }锛夈€?/summary>
public class GrcsProxyResult
{
    public bool Ok { get; set; }
    public int Code { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>鍦板浘缂撳瓨鍝嶅簲锛圙ET /api/wcs/map锛夈€?/summary>
public class MapCacheDto
{
    public string SavedAt { get; set; } = "";
    public int PathsCount { get; set; }
    public List<MapStationLite> Stations { get; set; } = [];
}

/// <summary>鍦板浘涓婁紶璐熻浇锛圥OST /api/wcs/map/upload锛夈€?/summary>
public class MapUploadPayload
{
    public string SavedAt { get; set; } = "";
    public int PathsCount { get; set; }
    public List<MapStationLite> Stations { get; set; } = [];
}
