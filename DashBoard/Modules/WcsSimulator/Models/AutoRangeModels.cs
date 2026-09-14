using Contracts.Dtos;

namespace Dashboard.Modules.WcsSimulator.Models;

/// <summary>
/// 选点范围解析辅助（RangeConfigDto 的数据类在 Contracts.Dtos；
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

    /// <summary>是否让站点与库存图标随地图缩放。库存地图启用，自动化选点保持固定像素大小。</summary>
    public bool ScaleSymbolsWithZoom { get; set; }

    /// <summary>选中时不放大点位本体，仅显示紧贴的缩放亮环。库存地图启用。</summary>
    public bool CompactSelection { get; set; }

    /// <summary>是否在选中或框选命中时显示站点文字。库存地图关闭，详情改由悬停提示提供。</summary>
    public bool ShowSelectedLabels { get; set; } = true;

    /// <summary>是否显示人工分拣台与实际分拣台之间的关联线。</summary>
    public bool ShowSortingLinks { get; set; }

    /// <summary>库存地图对已选中的人工分拣台右击时，显示实际分拣台关联菜单。</summary>
    public bool RightClickPeopleStationSelection { get; set; }
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
    /// <summary>可选的库存视觉状态：pallet、cargo、loaded、transit、locked、empty。</summary>
    public string? VisualKind { get; set; }
    /// <summary>库存页传入的状态提示；为空时使用通用站点类型提示。</summary>
    public string? Tooltip { get; set; }
    /// <summary>库存地图上的小型汇总标识，例如人工分拣台的“1/2”。</summary>
    public string? BadgeText { get; set; }
    /// <summary>实际分拣台所属人工分拣台，仅用于地图绘制关联线。</summary>
    public string? ParentMark { get; set; }
}
