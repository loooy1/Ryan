using WCSBackend.Modules.Wcs.Infrastructure;
using Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace WCSBackend.Modules.Wcs.Console.Controllers;

/// <summary>
/// GRCS 接口说明清单（/api/wcs/grcs-api-docs）。
/// 内置：GrcsHttpClient 中写死的 GRCS 接口（名称 / 方法 / 路径模板 / 描述 / 用到的地方 / 参数与请求体示例）。
/// 动态：功能模块（FeatureModuleStore，SQLite feature_modules 表）——用户在「信号交互」页新建模块后
/// 写入数据库，刷新本接口即自动出现；经通用转发 /api/wcs/forward 代发 GRCS。
/// </summary>
[ApiController]
[Route("api/wcs/grcs-api-docs")]
public class GrcsApiDocsController : ControllerBase
{
    private readonly FeatureModuleStore _modules;

    public GrcsApiDocsController(FeatureModuleStore modules) => _modules = modules;

    [HttpGet]
    public ActionResult<List<GrcsApiDocDto>> Get()
    {
        var result = Builtin.ToList();
        foreach (var m in _modules.GetAll())
        {
            result.Add(new GrcsApiDocDto
            {
                Name = "功能模块：" + (string.IsNullOrWhiteSpace(m.Name) ? m.ApiUrl : m.Name),
                Method = "POST",
                UrlTemplate = m.ApiUrl,
                Description = "功能模块自定义（从数据库读取）：在「信号交互 → 功能模块」创建，关联自动化模板任务各时机（起点前/起点后/终点后）触发，经后端通用转发 /api/wcs/forward 代发 GRCS；新建模块后回到本页刷新即自动出现。",
                UsedBy = "信号交互页（功能模块，SQLite 持久化）、自动化模板任务（起点/起点之后/终点）",
                Params = m.Params.Select(p => new GrcsApiParamDto
                {
                    Name = p.Name,
                    Type = "string",
                    Required = false,
                    Description = "取值来源：" + SourceName(p.Source)
                        + (p.Source == WorkValueSourceDto.Fixed && !string.IsNullOrWhiteSpace(p.FixedValue) ? $"（固定值：{p.FixedValue}）" : ""),
                }).ToList(),
                BodyExample = BuildBodyExample(m),
            });
        }
        return Ok(result);
    }

    private static string SourceName(WorkValueSourceDto s) => s switch
    {
        WorkValueSourceDto.StartPoint => "起点站点",
        WorkValueSourceDto.EndPoint => "终点站点",
        WorkValueSourceDto.TaskContainer => "任务容器/货物",
        WorkValueSourceDto.TaskWarehouse => "场景/仓库名",
        WorkValueSourceDto.TaskType => "任务类型",
        WorkValueSourceDto.TaskId => "任务编号",
        WorkValueSourceDto.Now => "当前时间",
        WorkValueSourceDto.ModuleContext => "回库任务预处理",
        WorkValueSourceDto.JsonLiteral => "JSON 固定值",
        _ => "固定值",
    };

    private static string? BuildBodyExample(FeatureModuleDto m)
    {
        if (m.Params.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        for (var i = 0; i < m.Params.Count; i++)
        {
            var p = m.Params[i];
            var placeholder = p.Source switch
            {
                WorkValueSourceDto.StartPoint => "<起点站点>",
                WorkValueSourceDto.EndPoint => "<终点站点>",
                WorkValueSourceDto.TaskContainer => "<任务容器编码>",
                WorkValueSourceDto.TaskWarehouse => "<场景名>",
                WorkValueSourceDto.TaskType => "<任务类型>",
                WorkValueSourceDto.TaskId => "<任务编号>",
                WorkValueSourceDto.Now => "2026-08-30T10:00:00+08:00",
                _ => p.FixedValue,
            };
            sb.Append($"  \"{p.Name}\": \"{placeholder}\"");
            if (i < m.Params.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static readonly List<GrcsApiDocDto> Builtin =
    [
        new()
        {
            Name = "TaskReceive",
            Method = "POST",
            UrlTemplate = "/api/v1/task_receive",
            Description = "任务组下发：一次提交一个任务组（GroupId 唯一标识），组内含多条任务（每任务：类型/容器/起始+目标站点/区域）。WCS 侧任务下发/模板执行/归巢之外的任务派发都走它。",
            UsedBy = "手动任务下发（任务组批量）、自动化模板任务执行",
            Params =
            [
                new() { Name = "GroupId", Type = "string", Required = true, Description = "任务组唯一编号（如 SimAuto_xxx，全局不重复）" },
                new() { Name = "MsgTime", Type = "string", Required = true, Description = "报文时间（ISO 格式）" },
                new() { Name = "PriorityCode", Type = "int", Required = false, Description = "优先级" },
                new() { Name = "Warehouse", Type = "string", Required = true, Description = "场景/仓库名（系统设置中的场景名称）" },
                new() { Name = "Tasks[]", Type = "array", Required = true, Description = "任务列表" },
                new() { Name = "Tasks[].TaskId", Type = "string", Required = true, Description = "任务编号（组内唯一）" },
                new() { Name = "Tasks[].TaskType", Type = "string", Required = true, Description = "任务类型（如 CONTAINER_CARRY_INBOUND / CARGO_CARRY_OUTBOUND 等）" },
                new() { Name = "Tasks[].ContainerCode", Type = "string", Required = false, Description = "容器/货物编码" },
                new() { Name = "Tasks[].StationCode", Type = "string[]", Required = true, Description = "站点 Mark 序列（起点→终点，多段）" },
                new() { Name = "Tasks[].AreaCode", Type = "string[]", Required = false, Description = "区域编码序列" },
            ],
            BodyExample = """
                {
                  "GroupId": "SimAuto_0000000001",
                  "MsgTime": "2026-08-30T10:00:00+08:00",
                  "PriorityCode": 50,
                  "Warehouse": "Show",
                  "Tasks": [
                    {
                      "TaskId": "SimAuto_0000000001_1",
                      "TaskType": "CONTAINER_CARRY_INBOUND",
                      "ContainerCode": "container_0001",
                      "StationCode": ["0100000108", "0100001427"],
                      "AreaCode": []
                    }
                  ]
                }
                """,
        },
        new()
        {
            Name = "VehicleOrder",
            Method = "POST",
            UrlTemplate = "/api/RawOrder/ChangeFloor",
            Description = "单车辆任务：指定某一台车执行纯移动（MOVE_ONLY）或搬运任务，一次一个目标站点序列。归巢调度/车辆移动/移动循环专用（并发量小、单发单收）。超时 7 秒。",
            UsedBy = "手动任务下发（MOVE_ONLY 车辆移动）、归巢模式调度、移动循环",
            Params =
            [
                new() { Name = "CreateTime", Type = "datetime", Required = true, Description = "创建时间" },
                new() { Name = "SceneName", Type = "string", Required = true, Description = "场景名（系统设置）" },
                new() { Name = "OrderType", Type = "string", Required = true, Description = "订单类型，当前固定 MOVE_ONLY（纯移动）" },
                new() { Name = "OrderId", Type = "string", Required = true, Description = "订单唯一编号（如 NestHome_xxx / MoveLoop_xxx）" },
                new() { Name = "OrderName", Type = "string", Required = false, Description = "订单名称" },
                new() { Name = "VehicleName", Type = "string", Required = true, Description = "目标车辆名（明确指定某台车）" },
                new() { Name = "Priority", Type = "int", Required = false, Description = "优先级（默认 50）" },
                new() { Name = "StationCodes", Type = "string[]", Required = true, Description = "目标站点 Mark 列表（按顺序执行）" },
                new() { Name = "ErrorCode", Type = "string", Required = false, Description = "预留错误码" },
            ],
            BodyExample = """
                {
                  "CreateTime": "2026-08-30T10:00:00+08:00",
                  "SceneName": "Show",
                  "OrderType": "MOVE_ONLY",
                  "OrderId": "NestHome_1A2B3C_5F7A",
                  "OrderName": "wcs模拟器归巢任务",
                  "VehicleName": "V01",
                  "Priority": 50,
                  "StationCodes": ["0100001427"],
                  "ErrorCode": ""
                }
                """,
        },
        new()
        {
            Name = "GetAllVehicles",
            Method = "GET",
            UrlTemplate = "/api/Vehicle/GetAllVehicles?sceneName={sceneName}",
            Description = "查询全部车辆及当前状态（在线/空闲/执行中/报错、电量、坐标、所在站点）。归巢模式选车队与巡检的车辆数据源。",
            UsedBy = "归巢模式（两步地图选车队、执行巡检）",
            Params =
            [
                new() { Name = "sceneName", Type = "string(query)", Required = true, Description = "场景名（系统设置）" },
            ],
        },
        new()
        {
            Name = "Cargo",
            Method = "GET",
            UrlTemplate = "/api/Cargo?pageNo={pageNo}&pageSize={pageSize}&SearchContextParams[Code]={code}&SearchContextParams[HomeStationScene]={scene}&SearchContextParams[IsLocked]={locked}",
            Description = "库存查询：分页返回全部货物/容器，支持编码 / 场景 / 锁定状态过滤。",
            UsedBy = "库存管理页（货物列表）",
            Params =
            [
                new() { Name = "pageNo", Type = "int(query)", Required = true, Description = "页码（从 1 起）" },
                new() { Name = "pageSize", Type = "int(query)", Required = true, Description = "每页条数（默认 2000）" },
                new() { Name = "SearchContextParams[Code]", Type = "string(query)", Required = false, Description = "货物/容器编码过滤" },
                new() { Name = "SearchContextParams[HomeStationScene]", Type = "string(query)", Required = false, Description = "场景名过滤" },
                new() { Name = "SearchContextParams[IsLocked]", Type = "string(query)", Required = false, Description = "锁定状态过滤（true/false）" },
            ],
        },
        new()
        {
            Name = "AutoContainerEnter",
            Method = "GET",
            UrlTemplate = "/AutoContainerEnter?sceneName={sceneName}&prefix={prefix}&num={num}&floor={floor}&type={type}",
            Description = "模拟生成容器入库：一次性生成 num 个容器（编码前缀 prefix）写入指定楼层/类型，用于测试库存链路。",
            UsedBy = "库存管理页（模拟入库按钮）",
            Params =
            [
                new() { Name = "sceneName", Type = "string(query)", Required = true, Description = "场景名（系统设置）" },
                new() { Name = "prefix", Type = "string(query)", Required = false, Description = "容器编码前缀（默认 container）" },
                new() { Name = "num", Type = "int(query)", Required = false, Description = "生成数量（默认 -1 = 全量？）" },
                new() { Name = "floor", Type = "int(query)", Required = false, Description = "楼层（默认 -1）" },
                new() { Name = "type", Type = "int(query)", Required = false, Description = "容器类型（默认 1）" },
            ],
        },
        new()
        {
            Name = "GetMap",
            Method = "GET",
            UrlTemplate = "/api/Map/GetMap?sceneName={sceneName}&getTypes=feMap",
            Description = "地图数据下载：按场景返回 feMap 类型的 zip 包（内含 map.json），解析出站点/路径/楼层用于全站散点图与任务选点。",
            UsedBy = "地图信息页（接口读取）",
            Params =
            [
                new() { Name = "sceneName", Type = "string(query)", Required = true, Description = "场景名（系统设置）" },
                new() { Name = "getTypes", Type = "string(query)", Required = false, Description = "地图类型，固定 feMap" },
            ],
        },
        new()
        {
            Name = "Forward（通用转发）",
            Method = "GET/POST/PUT/DELETE",
            UrlTemplate = "{自定义 URL}",
            Description = "通用转发：按用户自定义 URL 与方法把原始 JSON 报文直接转发给 GRCS（功能模块/信号自定义下发），URL 由前端配置，不经白名单限制。",
            UsedBy = "信号交互页（功能模块自定义报文）、通用下发入口",
            Params =
            [
                new() { Name = "url", Type = "string", Required = true, Description = "目标 URL（相对路径或完整地址）" },
                new() { Name = "method", Type = "string", Required = true, Description = "HTTP 方法（GET/POST/PUT/DELETE）" },
                new() { Name = "body", Type = "object", Required = false, Description = "原始 JSON 报文（POST/PUT 时）" },
            ],
        },
        new()
        {
            Name = "Ping（存活探测）",
            Method = "GET",
            UrlTemplate = "/",
            Description = "存活探测：GET GRCS 根路径，能拿到任意状态码即视为可达（2 秒短超时），供前端连通性轮询与健康显示。",
            UsedBy = "地图信息页（GRCS 连接状态）、各页面连通性提示",
            Params =
            [
                new() { Name = "（无参数）", Type = "-", Required = false, Description = "直接请求根路径" },
            ],
        },
    ];
}