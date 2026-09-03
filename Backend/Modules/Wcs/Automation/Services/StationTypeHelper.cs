namespace GrcsBackend.Modules.Wcs.Automation.Services;

/// <summary>站点类型位解码工具（纯静态，供模板校验与任务下发共用）。</summary>
public static class StationTypeHelper
{
    public static string BitsName(int bits)
    {
        if (bits == 0) return "不限";
        var names = new List<string>();
        if ((bits & Contracts.Dtos.MapStationTypeBits.NormalRoad) != 0) names.Add("普通路");
        if ((bits & Contracts.Dtos.MapStationTypeBits.HighWay) != 0) names.Add("高速路");
        if ((bits & Contracts.Dtos.MapStationTypeBits.PeopleStation) != 0) names.Add("人工位");
        if ((bits & Contracts.Dtos.MapStationTypeBits.StorageLocation) != 0) names.Add("储位");
        if ((bits & Contracts.Dtos.MapStationTypeBits.TransferPoint) != 0) names.Add("接驳位");
        if ((bits & Contracts.Dtos.MapStationTypeBits.Parking) != 0) names.Add("停车");
        if ((bits & Contracts.Dtos.MapStationTypeBits.Charging) != 0) names.Add("充电");
        if ((bits & Contracts.Dtos.MapStationTypeBits.PickingStation) != 0) names.Add("分拣台");
        if ((bits & Contracts.Dtos.MapStationTypeBits.PeopleStation) != 0) names.Add("人工台");
        if ((bits & Contracts.Dtos.MapStationTypeBits.Elevator) != 0) names.Add("电梯");
        if ((bits & Contracts.Dtos.MapStationTypeBits.Other) != 0) names.Add("其他");
        return names.Count == 0 ? $"未知({bits})" : string.Join("+", names);
    }
}