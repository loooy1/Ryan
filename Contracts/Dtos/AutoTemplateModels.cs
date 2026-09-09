namespace Contracts.Dtos;

/// <summary>自动化模板步骤类型（线性有序执行）。</summary>
public static class AutoStepKinds
{
    public const string PickPallet = "PickPallet";   // 随机选托盘（从库存快照）
    public const string PickCargo = "PickCargo";     // 随机选货物（从库存快照）
    public const string PickLoadedPallet = "PickLoadedPallet"; // 随机选带货托（带货物的托盘，从库存快照）
    public const string RunTemplate = "RunTemplate"; // 用前置步骤选出的托盘/货物执行「已生成的任务模板」
}

/// <summary>自动化模板单步：选托盘 / 选货物 / 执行任务模板。</summary>
public class AutoStepDto
{
    /// <summary>步骤类型：PickPallet / PickCargo / RunTemplate。</summary>
    public string Kind { get; set; } = AutoStepKinds.PickPallet;

    /// <summary>PickPallet 专用：托盘过滤（Empty=空托 / Loaded=带货托 / Any=任意）。</summary>
    public string PalletFilter { get; set; } = "Empty";

    /// <summary>RunTemplate 专用：引用的任务模板 Value（对应 task_templates 表）。</summary>
    public string TemplateValue { get; set; } = "";

    /// <summary>步骤备注（界面展示）。</summary>
    public string Label { get; set; } = "";

    /// <summary>RunTemplate 专用：容器是否使用前置步骤挑选的托盘/货物号。
    /// true（默认）：容器取自前置「选托盘/选货物」；false：容器按模板 ContainerPrefix 自动生成。
    /// 旧模板字段（PickedStepIndex 未设置时生效）；新版请用 PickedStepIndex 精确指定引用步骤。</summary>
    public bool UsePickedContainer { get; set; } = true;

    /// <summary>RunTemplate 专用：容器来源。
    /// 0 = 按模板 ContainerPrefix 自动生成；-1 = 最近前置挑选的容器（旧逻辑）；
    /// &gt;0 = 引用前置第 N 步挑选的容器号（选托盘=托盘号、选货物=货物号、RunTemplate=该步最终容器号）。
    /// 未设置（0）且 UsePickedContainer=true 的旧数据按 -1 处理。</summary>
    public int PickedStepIndex { get; set; } = 0;

    /// <summary>RunTemplate 专用：起点是否取自前置步骤的终点（链路衔接）。
    /// true（默认）：起点 = 上一步的终点（选托盘时即托盘所在站）；false：起点按模板 Start.StationTypeBits 在选点范围内自行选点。</summary>
    public bool UsePickedStart { get; set; } = true;

    /// <summary>等待完成再继续下一步：true 时该 RunTemplate 步骤会等任务（及其终点模块）完成后再走下一步。
    /// 含终点(End)模块的步骤强制等待模块 success；无终点模块时按 FINISHED 阶段等待。默认开启，可单步关闭。</summary>
    public bool WaitForFinish { get; set; } = true;
}

/// <summary>自动化模板（用户自定义，持久化 auto_templates 表，跨浏览器共享）。</summary>
public class AutoTemplateDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<AutoStepDto> Steps { get; set; } = [];
}
