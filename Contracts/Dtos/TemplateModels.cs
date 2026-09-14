namespace Contracts.Dtos;

/// <summary>参数取值来源（与前端 WorkValueSource 对齐，JSON 按字符串序列化以保持可读）。</summary>
public enum WorkValueSourceDto
{
    Fixed = 0,
    StartPoint = 1,
    EndPoint = 2,
    TaskContainer = 3,
    TaskWarehouse = 4,
    TaskType = 5,
    TaskId = 6,
    Now = 7,
    TaskPallet = 8,
    TaskCargo = 9,
    /// <summary>模块执行前预处理产生的变量；当前用于回库任务预处理。</summary>
    ModuleContext = 10,
    /// <summary>固定 JSON 值，例如 false、true、12；用于保留请求字段类型。</summary>
    JsonLiteral = 11,
}

/// <summary>功能模块参数（参数名 + 取值来源 + 固定值）。</summary>
public class WorkParamDto
{
    public string Name { get; set; } = "";

    public WorkValueSourceDto Source { get; set; } = WorkValueSourceDto.Fixed;

    public string FixedValue { get; set; } = "";
}

/// <summary>任务模板中的一个点（起点/终点）：标签 + 关联模块（之前/之后）+ 站点类型约束。</summary>
public class TaskPointDto
{
    public string Label { get; set; } = "";

    /// <summary>此点「之前」模块（起点之前 = 下发前执行）。</summary>
    public List<string> BeforeModules { get; set; } = [];

    /// <summary>此点「之后」模块（起点之后 = 下发 success 后；终点之后 = FINISHED 后）。</summary>
    public List<string> AfterModules { get; set; } = [];

    /// <summary>站点类型位约束（MapStationTypeBits 组合）。</summary>
    public int StationTypeBits { get; set; }
}

/// <summary>任务类型模板 DTO（与前端 TaskTypeTemplate 同构）。</summary>
public class TaskTemplateDto
{
    public string Value { get; set; } = "";

    public string Label { get; set; } = "";

    public string Description { get; set; } = "";

    public string Category { get; set; } = "";

    public bool NeedsContainer { get; set; } = true;

    public string ContainerPrefix { get; set; } = "";

    public bool RandomContainer { get; set; }

    public TaskPointDto Start { get; set; } = new();

    public TaskPointDto End { get; set; } = new();
}

/// <summary>功能模板 DTO（与前端 WcsModule 同构）。</summary>
public class FeatureModuleDto
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string ApiUrl { get; set; } = "";

    /// <summary>模块 HTTP 请求前执行的预处理效果。</summary>
    public string PreExecutionEffect { get; set; } = "";

    /// <summary>模块成功后要执行的库存动作；空值表示不影响库存。</summary>
    public string InventoryEffect { get; set; } = "";

    public List<WorkParamDto> Params { get; set; } = [];
}

/// <summary>功能模块可配置的库存动作。</summary>
public static class ModuleInventoryEffects
{
    public const string None = "";
    public const string CargoArrival = "cargo_arrival";
    public const string CargoRemoval = "cargo_removal";
    public const string CreateReturnTask = "create_return_task";
}

/// <summary>模块 HTTP 请求前的预处理动作。</summary>
public static class ModulePreExecutionEffects
{
    public const string None = "";
    public const string PrepareReturnTask = "prepare_return_task";
}
