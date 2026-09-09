namespace Contracts.Entities;

/// <summary>
/// WCS 储位行（wcs_slots 表）：一行管理托盘+货物。
/// 列：pallet_code/cargo_code（号码）+ pallet_status/cargo_status（状态）。
/// 状态值：""（无）/ ready / picked（已选未下发，在储位）/ transit（LOAD_FINISH 已取走，号码保留作出发记录）/
///         fail（下发失败待确认）/ grcs_lock（GRCS 外部锁定）。
/// 分类：有托有货=带货托；有货无托=纯货物；有托无货=纯托盘；都无=空。
/// </summary>
public class WcsSlotRow
{
    public string Mark { get; set; } = "";
    public string PalletCode { get; set; } = "";
    public string PalletStatus { get; set; } = "";
    public string CargoCode { get; set; } = "";
    public string CargoStatus { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}