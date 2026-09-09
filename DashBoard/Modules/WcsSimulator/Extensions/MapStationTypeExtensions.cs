using Contracts.Dtos;
using Dashboard.Modules.WcsSimulator.Models;

namespace Dashboard.Modules.WcsSimulator.Extensions;

/// <summary>
/// WCS 下发编码转换扩展方法。
/// 背景：GRCS 地图中同一物理点的上层储位与下层接驳位（/分拣台）共用同一个 Mark，
/// 靠 _1/_0 后缀区分层级；任务下发的 StationCode 必须带后缀，否则 GRCS 无法定位
/// 目标层。这些方法负责 Mark ↔ 下发编码的双向转换。
/// </summary>
public static class MapStationTypeExtensions
{
    /// <summary>
    /// 把站点 Mark 转为 WCS 下发编码：储位 → mark_1（放货层），
    /// 接驳位/分拣台 → mark_0（车辆停靠层），其他不变。
    /// </summary>
    public static string ToWcsCode(this MapStationLite st)
        => st.Mark.ToWcsCode(st.StationType);

    /// <summary>把站点 Mark 转为 WCS 下发编码（已知站点类型位，不必先查地图）。</summary>
    public static string ToWcsCode(this string mark, int stationType)
    {
        if ((stationType & MapStationTypeBits.StorageLocation) != 0) return mark + "_1";
        if ((stationType & (MapStationTypeBits.TransferPoint | MapStationTypeBits.PickingStation | MapStationTypeBits.PeopleStation)) != 0) return mark + "_0";
        return mark;
    }

    /// <summary>
    /// 先在地图中查该点（兼容已带 _0/_1 后缀的编码），再按站点类型转换后缀；
    /// 地图查不到时原样返回（兜底：不破坏台账里的既有编码）。
    /// 用于把台账/卡片里保存的站点编码规范化后下发。
    /// </summary>
    public static string ToWcsCode(this string mark, IEnumerable<MapStationLite> mapStations)
    {
        if (string.IsNullOrEmpty(mark)) return mark;
        var raw = mark.ToMark();
        var st = mapStations.FirstOrDefault(s => s.Mark.Equals(raw, StringComparison.OrdinalIgnoreCase));
        return st != null ? st.ToWcsCode() : mark;
    }

    /// <summary>
    /// 剥掉 _0/_1 后缀，返回裸 Mark 编码（如 0100000108_1 → 0100000108）；无后缀原样返回。
    /// 用于把下发编码还原为地图字典键（MapStation.Mark）查找站点。
    /// </summary>
    public static string ToMark(this string stationCode)
    {
        if (string.IsNullOrEmpty(stationCode)) return stationCode ?? "";
        var span = stationCode.AsSpan();
        if (span.Length > 2 && (span[^2..] is "_0" or "_1"))
            return span[..^2].ToString();
        return stationCode;
    }
}
