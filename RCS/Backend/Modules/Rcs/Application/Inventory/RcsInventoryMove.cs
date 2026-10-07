namespace RCSBackend.Modules.Rcs.Application.Inventory;

/// <summary>库存服务所需的搬运数据，不依赖路径规划或车辆协议对象。</summary>
public sealed record RcsInventoryMove(string TaskId, string MapCode, string ContainerCode,
    string FetchPointCode, string PutPointCode);
