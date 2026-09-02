using GrcsBackend.Contracts.Dtos;

namespace GRCS.Dashboard.Modules.WcsSimulator.Models;

/// <summary>
/// 选点范围解析辅助（RangeConfigDto 的数据类在 GrcsBackend.Contracts.Dtos；
/// 本类只保留前端专用的静态解析逻辑）。
/// </summary>
public static class RangeConfigHelpers
{
    /// <summary>解析用户输入文本为 Mark 白名单（支持中文/英文逗号、空格、换行分隔）。</summary>
    public static List<string> ParseMarks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// 站点地图框选器（StationMapPicker）传给 JS 的配置。
/// 数据口径：Stations = 全部站点（含禁用，禁用置灰不可选）；
/// Floors 取启用站点楼层去重排序；Preselected = 既有 Mark 白名单（打开时预选中，增量编辑）。
/// 白名单自身不参与候选过滤（否则越选越窄），关闭回写时 Mark 与地图大小写不敏感匹配。
/// </summary>
public class StationMapPickerConfig
{
    /// <summary>可选楼层（启用站点楼层去重升序）。</summary>
    public List<int> Floors { get; set; } = [];

    /// <summary>默认楼层（范围卡片楼层=全部时取最低层；否则取范围楼层）。</summary>
    public int InitialFloor { get; set; }

    /// <summary>候选站点（含禁用，JS 端按 StaEnable 置灰不可选）。</summary>
    public List<StationMapPickerStation> Stations { get; set; } = [];

    /// <summary>打开时预选中的既有 Mark 白名单。</summary>
    public List<string> Preselected { get; set; } = [];
}

/// <summary>框选器画布里的单个站点（精简字段，仅供 JS 绘制/命中）。</summary>
public class StationMapPickerStation
{
    public string Mark { get; set; } = "";
    public int StationType { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Floor { get; set; }
    public bool StaEnable { get; set; }
}