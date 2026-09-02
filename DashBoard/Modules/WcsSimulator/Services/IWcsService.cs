using GrcsBackend.Contracts.Dtos;

namespace GRCS.Dashboard.Modules.WcsSimulator.Services;

/// <summary>
/// WCS 服务接口：任务下发 + 信号模拟。
/// 当前使用 MockWcsService（开发阶段），对接后端时切换实现。
/// </summary>
public interface IWcsService
{
    // ── 车辆任务（GRCS 运力调度接口，非 WCS 协议）──

    /// <summary>发送车辆任务（移动/换层/充电，POST /api/RawOrder/ChangeFloor）。</summary>
    Task<(bool Ok, int StatusCode, string Json)> SendVehicleOrderAsync(
        string baseUrl, VehicleOrderRequest payload);

    // ── 库存查询 ──

    /// <summary>查询容器库存（GET /api/Cargo，支持按容器编码 / 锁定状态过滤 + 分页；场景按后端设置）。</summary>
    Task<(bool Ok, int StatusCode, string Json)> QueryCargoInventoryAsync(
        string baseUrl, string? code = null, string? scene = null, string? locked = null,
        int pageNo = 1, int pageSize = 2000);

    /// <summary>自动生成容器入库（GET /AutoContainerEnter，场景按后端设置）。</summary>
    Task<(bool Ok, int StatusCode, string Json)> AutoContainerEnterAsync(string baseUrl, string sceneName,
        string prefix = "container", int num = -1, int floor = -1, int type = 1);

    // ── 任务阶段（WCS 后端管理接口 /api/wcs）──

    /// <summary>删除指定任务的所有阶段事件（DELETE /api/wcs/task-stages/{taskId}）。</summary>
    Task<(bool Ok, int StatusCode, string Json)> DeleteTaskStageAsync(string baseUrl, string taskId);
}
