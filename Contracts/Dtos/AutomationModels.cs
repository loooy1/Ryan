using System.Globalization;
using Contracts.Dtos;

namespace Contracts.Dtos;

/// <summary>精简站点信息（与前端 MapStationLite 同构，从地图上传/GRCS 拉取后缓存）。</summary>
public class MapStationLite
{
    public string Mark { get; set; } = "";
    public int StationType { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Floor { get; set; }
    public bool StaEnable { get; set; }
    public List<string> CargoAreas { get; set; } = [];
    public int SupportLoadState { get; set; }
    public bool AllowTurn { get; set; }
    public bool AllowAvoid { get; set; }
    public bool AllowStop { get; set; }

    /// <summary>下发编码：储位 → mark_1（放货层），接驳位/分拣台 → mark_0（车辆停靠层）。</summary>
    public string ToWcsCode()
    {
        const int StorageLocation = 4, TransferPoint = 8, PickingStation = 64, PeopleStation = 128;
        if ((StationType & StorageLocation) != 0) return Mark + "_1";
        if ((StationType & (TransferPoint | PickingStation | PeopleStation)) != 0) return Mark + "_0";
        return Mark;
    }
}

/// <summary>站点类型位标志（与前端共用：按位与判断、下发 _0/_1 后缀、分拣台筛选）。</summary>
public static class MapStationTypeBits
{
    public const int NormalRoad = 1;
    public const int HighWay = 2;
    public const int StorageLocation = 4;
    public const int TransferPoint = 8;
    public const int Parking = 16;
    public const int Charging = 32;
    public const int PickingStation = 64;
    public const int PeopleStation = 128;
    public const int Elevator = 256;
    public const int Other = 512;

    /// <summary>可筛选/展示的类型位列表（位值 + 中文名）：地图信息页筛选下拉与类型列展示共用。</summary>
    public static readonly (int Bit, string Name)[] All =
    [
        (NormalRoad, "普通道路"),
        (HighWay, "高速路"),
        (StorageLocation, "储位"),
        (TransferPoint, "接驳位"),
        (Parking, "停车位"),
        (Charging, "充电点"),
        (PickingStation, "分拣点"),
        (PeopleStation, "人工拣选台"),
        (Elevator, "电梯点"),
        (Other, "其他"),
    ];

    /// <summary>解码一个站点的类型位，返回其中文名列表（如 5 → ["普通道路", "储位"]）。</summary>
    public static List<string> DecodeNames(int type)
    {
        var names = new List<string>();
        foreach (var (bit, name) in All)
        {
            if ((type & bit) != 0) names.Add(name);
        }
        if (names.Count == 0) names.Add($"未配置({type})");
        return names;
    }
}

/// <summary>选点范围配置（与前端 AutoRangeConfig 同构；范围开启后只从限定池抽点）。
/// 简化后仅按 楼层 + Mark 白名单 过滤（站点类型不再参与）。</summary>
public class RangeConfigDto
{
    public bool Enabled { get; set; }
    /// <summary>历史遗留字段（不再参与过滤，统一为 0）。</summary>
    public int TypeFilter { get; set; }
    public int FloorFilter { get; set; }
    public List<string> Marks { get; set; } = [];

    /// <summary>按范围限制过滤候选站点池（楼层 + Mark 白名单，AND 关系）。</summary>
    public List<MapStationLite> ApplyTo(IEnumerable<MapStationLite> stations)
    {
        if (!Enabled) return stations.ToList();
        IEnumerable<MapStationLite> pool = stations;
        if (FloorFilter != 0) pool = pool.Where(s => s.Floor == FloorFilter);
        if (Marks.Count > 0)
        {
            var marks = Marks.Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            pool = pool.Where(s => marks.Contains(s.Mark));
        }
        return pool.ToList();
    }
}

/// <summary>运行配置（GRCS 地址/场景名，前端「地图信息」页保存 PUT 到后端，代理与自动化服务读取）。</summary>
public class WcsSettingsDto
{
    public string GrcsBaseUrl { get; set; } = "http://localhost:8224";
    public string SceneName { get; set; } = "Show";
}

/// <summary>自动化运行状态（GET /api/wcs/auto/status 快照）。</summary>
public class InventoryCountsDto
{
    public int EmptyPallets { get; set; }
    public int LoadedPallets { get; set; }
    public int Cargos { get; set; }
    public int PairedCargos { get; set; }
}

/// <summary>库存明细条目（仅列出有货/托的储位，不包含空储位）。</summary>
public class InventoryDetailItem
{
    /// <summary>容器号（托盘号或货物号）。</summary>
    public string Code { get; set; } = "";
    /// <summary>当前所在站点（空 = 在途/无站点记录）。</summary>
    public string? Station { get; set; }
    /// <summary>带货托关联的货物号（纯空托/纯货物为 null）。</summary>
    public string? CargoCode { get; set; }
    /// <summary>锁定状态：transit 在途 / picked 已选未下发 / fail 下发失败（仅锁定明细）。</summary>
    public string? Status { get; set; }
}

/// <summary>库存分类汇总 + 明细（GET /api/wcs/auto/inventory-summary）。</summary>
public class InventorySummaryDto
{
    public int Empty { get; set; }
    public int Loaded { get; set; }
    public int Cargo { get; set; }
    /// <summary>锁定中 = 非在途锁定单元数（picked/fail；货+托同储位算一个）。</summary>
    public int Locked { get; set; }
    /// <summary>在途 = transit 单元数（已取走，号码保留作出发记录；货+托同储位算一个）。</summary>
    public int Transit { get; set; }
    public List<InventoryDetailItem> EmptyItems { get; set; } = [];
    public List<InventoryDetailItem> LoadedItems { get; set; } = [];
    public List<InventoryDetailItem> CargoItems { get; set; } = [];
    public List<InventoryDetailItem> LockedItems { get; set; } = [];
    public List<InventoryDetailItem> TransitItems { get; set; } = [];
}

/// <summary>日志条目（自动化/批量执行共用，带自增 Id 供前端 sinceId 增量拉取）。</summary>
public class LogEntryDto
{
    public long Id { get; set; }
    public string Time { get; set; } = "";
    public string Message { get; set; } = "";
    public string Color { get; set; } = "#94a3b8";
}

/// <summary>日志轮次（每轮下发一个标题，含该轮所有条目）。</summary>
public class LogRoundDto
{
    public string RoundId { get; set; } = "";
    public string ParentRoundId { get; set; } = "";
    public string Title { get; set; } = "";
    public string StartTime { get; set; } = "";
    public bool Completed { get; set; }
    public List<LogEntryDto> Entries { get; set; } = new();
}

/// <summary>任务台账条目（与前端 TaskLedgerEntry 同构；ContainerCode 恒为托盘号、CargoCode 恒为货物号）。</summary>
public class TaskLedgerEntry
{
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public string CargoCode { get; set; } = "";
    public List<string> StationCode { get; set; } = [];
    public string Warehouse { get; set; } = "";
    public string Time { get; set; } = "";
    public bool Ok { get; set; }
    public int StatusCode { get; set; }
}

/// <summary>GRCS /api/Cargo 库存查询响应（托盘与货物共用，靠 Code 前缀区分）。</summary>
public class CargoQueryResult
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public CargoPagedData? Data { get; set; }
}

public class CargoPagedData
{
    public int TotalCount { get; set; }
    public List<CargoInventoryItem>? Records { get; set; }
}

public class CargoInventoryItem
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public string? HomeStationMark { get; set; }
    public string? HomeStationScene { get; set; }
    public string? HomeCargoAreaName { get; set; }
    public bool IsLoaded { get; set; }
    public string? RobotId { get; set; }
    public bool IsLocked { get; set; }
    public string? CurrentStationCode { get; set; }
    public string? CurrentCargoAreaName { get; set; }
    public string? CurrentOrderId { get; set; }

    public bool IsPallet() => Code?.Contains("Container", StringComparison.OrdinalIgnoreCase) ?? false;
    public bool IsCargo() => Code?.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ?? false;
}

/// <summary>发送给 GRCS 的任务组（/api/v1/task_receive）。</summary>
public class WcsTaskGroup
{
    public string GroupId { get; set; } = "";
    public string MsgTime { get; set; } = "";
    public int PriorityCode { get; set; }
    public string Warehouse { get; set; } = "";
    public List<WcsTaskItem> Tasks { get; set; } = [];
}

public class WcsTaskItem
{
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string ContainerCode { get; set; } = "";
    public List<string> StationCode { get; set; } = [];
    public List<string> AreaCode { get; set; } = [];
}

/// <summary>车辆任务请求（/api/RawOrder/ChangeFloor，MOVE_ONLY 纯移动）。</summary>
public class VehicleOrderRequest
{
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public string SceneName { get; set; } = "";
    public string OrderType { get; set; } = "MOVE_ONLY";
    public string OrderId { get; set; } = "";
    public string OrderName { get; set; } = "";
    public string? VehicleName { get; set; }
    public int Priority { get; set; }
    public List<string> StationCodes { get; set; } = [];
    public string ErrorCode { get; set; } = "";
}

/// <summary>车辆信息（GRCS /api/Vehicle/GetAllVehicles 的 VehicleView 解析）。</summary>
public class VehicleInfoDto
{
    public string Name { get; set; } = "";
    public string ExecutionState { get; set; } = "";
    public string UtilizationState { get; set; } = "";
    public string CurrentTransportOrder { get; set; } = "";
    public bool IsOnline { get; set; }
    public double Power { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string Location { get; set; } = "";

    /// <summary>归巢就绪判定：在线 + 空闲 + 自动调度 + 无当前任务。</summary>
    public bool IsReady => IsOnline
        && string.Equals(ExecutionState, "IDLE", StringComparison.OrdinalIgnoreCase)
        && string.Equals(UtilizationState, "AUTOMATIC", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(CurrentTransportOrder);
}

/// <summary>归巢模式配置（地图框选巢区站点 Mark 列表，SQLite 持久化，与选点范围 auto_range 相互独立）。</summary>
public class NestConfigDto
{
    public List<string> Marks { get; set; } = [];
}

/// <summary>归巢模式状态（SignalR NestStats 广播 + GET nest/status）。</summary>
public class NestStatsDto
{
    public bool Running { get; set; }
    public string? LastRunAt { get; set; }
    /// <summary>本次锁定的车队（只调度这些车；与就绪车全量区分）。</summary>
    public List<string> PoolVehicles { get; set; } = [];
    public int Ok { get; set; }
    public int Fail { get; set; }
    public string? LastError { get; set; }
    /// <summary>巢区目标点总数（区域内启用站点）。</summary>
    public int TargetTotal { get; set; }
    /// <summary>巢区已被车占用的目标点数。</summary>
    public int TargetOccupied { get; set; }
    /// <summary>已下发、车正在前往途中的目标点数（等待到达，不重复派车）。</summary>
    public int TargetAssigned { get; set; }
    /// <summary>本次归巢是否正常执行完成（true=完成并已清空车队与巢区；false=手动停止/失败，保留配置）。</summary>
    public bool Completed { get; set; }
}

/// <summary>异常记录-复现请求体（VehicleCode 覆盖车号，空串=清空）。</summary>
public class ExceptionRecordReproduceRequest
{
    public string? VehicleCode { get; set; }
}

/// <summary>站点锁条目（流程终点任务 FINISHED 后释放）。</summary>
public class StationLockEntry
{
    public string TaskId { get; set; } = "";
}

/// <summary>地图上传请求（POST /api/wcs/map/upload）。</summary>
public class MapUploadDto
{
    public string SavedAt { get; set; } = "";
    public int PathsCount { get; set; }
    public List<MapStationLite> Stations { get; set; } = [];
}
