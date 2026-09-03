namespace GrcsBackend.Contracts.Entities;

/// <summary>
/// WCS 自持库存账本行（wcs_inventory 表）：容器/货物一条记录。
/// 自动化任务选池/占用/释放的唯一事实源；GRCS 仅在「同步」时全量重建。
/// Status: idle（空闲可选）/ picked（已选未下发）/ busy（任务在途，task_id 记录占用任务）。
/// </summary>
public class WcsInventoryRow
{
    public string Code { get; set; } = "";
    public string Station { get; set; } = "";
    public string HomeMark { get; set; } = "";
    public string CargoCode { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string TaskId { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}