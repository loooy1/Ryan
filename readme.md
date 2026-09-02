# WCS 模拟器（Ryan）

GRCS 外围系统的 WCS 部分：后端（管理面）对 GRCS 核心系统暴露 WCS 协议回调接口（`/api/v1/*`），对前端提供控制台查询/管理接口（`/api/wcs/*`）；前端（Blazor WASM 模拟器）模拟 WCS 的各类作业/信号交互，并提供台账与监控页面。

> GRCS 核心后端（8224）是另一个系统，不在本仓库。

---

## 目录结构

```
Running_code/
├─ Solutions/     解决方案：Ryan.sln（组织 Backend + Contracts + DashBoard 三项目）
├─ Backend/       GrcsBackend：ASP.NET Core Web API 后端（管理面，端口 8230）
├─ DashBoard/     GRCS.Dashboard：Blazor WebAssembly 前端（模拟器）
├─ Contracts/     GrcsBackend.Contracts：共享契约类库（前后端共同引用）
└─ 数据库备份/      grcs.db 备份（不进版本库）
```

单仓库管理，GitHub：https://github.com/loooy1/Ryan

## 技术栈

### 后端 Backend（GrcsBackend）

| 项 | 内容 |
|---|---|
| 框架 | .NET 10（`net10.0`），ASP.NET Core Web API（`Microsoft.NET.Sdk.Web`） |
| 数据库 | SQLite（`Microsoft.EntityFrameworkCore.Sqlite` 10.0.9，纯托管 bundle，免安装）；库文件 `grcs.db` 随编译输出复制 |
| ORM | EF Core 10：模块化单体，`OnModelCreating` 里 `ApplyConfigurationsFromAssembly` 反射扫描（新增表零注册）、`AddDbContextFactory` 短生命周期模式 |
| 映射 | Mapster 10.0.12（`WcsMapping.cs` 全局注册，契约 DTO ↔ 实体统一映射） |
| 序列化 | Newtonsoft.Json 10.0.9，`yyyy-MM-dd HH:mm:ss.fff`（必须与 GRCS 解析格式对齐） |
| 实时推送 | SignalR（`/hubs/task-stages`） |
| HTTP 出站 | `IHttpClientFactory`（`GrcsHttpClient` 调用 GRCS 8224） |
| 多标签页互斥 | `AutomationGate`（轮询/批量互斥闸，全后端只有一个执行者） |

### 契约 Contracts（GrcsBackend.Contracts）

`net8.0` 纯 POCO（无序列化特性），前后端共享：

```
Contracts/
├─ Entities/      数据库实体
└─ Dtos/          前后端传输模型（含 FrontendSharedModels 前端共享模型）
```

### 前端 DashBoard（GRCS.Dashboard）

| 项 | 内容 |
|---|---|
| 框架 | .NET 10（`net10.0`），Blazor WebAssembly（`Microsoft.NET.Sdk.BlazorWebAssembly`） |
| UI | MudBlazor 8.15.0 |
| Excel 导出 | ClosedXML 0.105.1 |
| 实时 | SignalR 客户端（`TaskStageHub`，任务看板事件推送，前端不轮询 task-stages） |
| 持久化 | 浏览器 localStorage（`LocalStoreService`，如当前项目记忆） |

## 层与引用关系

- 实体与 DTO 统一定义在 `Contracts`，`Backend` 与 `DashBoard` 均通过 `ProjectReference ..\Contracts\GrcsBackend.Contracts.csproj` 引用；DTO ↔ 实体映射由后端 `WcsMapping.cs`（Mapster）统一完成。
- 后端为模块化单体：每个业务域一个 `Modules/<域>/` 目录，通过 `XxxModuleExtensions.AddXxxModule()` 在 `Program.cs` 挂接注册。

```
Backend/Modules/Wcs/
├─ Proxy/              对 GRCS 的代理/转发（GrcsProxyController、GrcsHttpClient）
├─ Automation/         自动下发引擎：模板执行 AutoTemplateRunner、纯移动循环 MoveLoopRunner、
│                      归巢 NestRunner、统一模块执行 ModuleRunService、终点执行器
│                      FinishedModuleWatcher、信号自动放行 SignalAutoHostedService
├─ Console/            控制台查询/管理（Controllers + Services + Models）
├─ Realtime/           SignalR（TaskStageRealtimeHub，任务阶段事件实时推送）
└─ Infrastructure/     数据层（DbContext + 各 Store + 日志服务 + WcsMapping）
```

```
DashBoard/
├─ Layout/                 MainLayout（两层侧边栏导航）、BackendStatus、AutomationStatusBar
├─ Modules/WcsSimulator/   WCS 模拟器模块
│   ├─ Pages/              9 个页面（见下方路由表）
│   ├─ Services/           WcsApiClient（后端遥控壳）、AutomationHub（1s 轮询中枢）、
│   │                      TaskStageHub（SignalR）、各瘦壳服务、BackendHealthService
│   ├─ Models/             前端模型（TaskTypeRegistry / ModuleRegistry / MapFileModels 等）
│   └─ Extensions/         扩展
└─ _Imports.razor          全局 using 清单（新增模块在此补充命名空间）
```

## 运行

后端（workdir = `Backend`，端口由 `appsettings.json` 的 `Urls` 决定，默认 http://0.0.0.0:8230）：

```powershell
dotnet build GrcsBackend.csproj -v q --nologo
dotnet run
```

前端（workdir = `DashBoard`）：

```powershell
dotnet build GRCS.Dashboard.csproj -v q --nologo
dotnet run
```

VS 联调：打开 `Solutions\Ryan.sln`（三项目一键编译运行）。

> 注意：
> - 项目面向 `net10.0`，VS 需 2026（18.x）或更高；命令行使用 .NET 10 SDK 可直接编译运行。
> - SQLite 表结构变更/新接口上线后需**重启后端**生效；前端可硬刷新（Ctrl+F5）。

## API 一览

| 前缀 | 用途 |
|---|---|
| `api/wcs` | WCS 控制台总入口（`WcsConsoleController`）+ 通用转发（`ForwardController`） |
| `api/wcs/auto` | 自动化：状态快照/日志/执行（`AutomationConsoleController`） |
| `api/wcs/grcs` | 对 GRCS 的代理调用（`GrcsProxyController`） |
| `api/wcs/exception-records` | 异常记录台账（含 `GET /projects`、`DELETE projects/{name}?password=`、`POST /{id}/reproduce`） |
| `api/wcs/project-logs` | 项目记录台账（每日日程，同上项目隔离/删除密码） |
| `api/wcs/ledger` / `map` / `templates` / `modules` | 台账、地图缓存、任务模板/功能模块 |
| `api/wcs/signal-confirm` | 信号确认 |
| `api/wcs/mocks` + `api/{*path}`（Order 999） | Mock 入站：`api/v1/*` 之外的任意路径兜底模拟 |
| `api/wcs/grcs-api-docs` | GRCS 接口说明清单（内置静态 + 数据库动态） |
| `hubs/task-stages` | SignalR：任务阶段事件实时推送 |

## 页面路由（前端）

| 路由 | 页面 | 说明 |
|---|---|---|
| `/wcs-simulator/dispatch` | TaskDispatch | 手动任务下发（容器/移动任务、模板化下发） |
| `/wcs-simulator/inventory` | Inventory | 库存管理 |
| `/wcs-simulator/map-reader` | MapReader | 地图信息 |
| `/wcs-simulator/automation` | AutomationTasks | 自动化任务（模板执行、审批卡片、断点续跑） |
| `/wcs-simulator/signal-interaction` | SignalInteraction | 信号交互（长连接化、信号确认） |
| `/wcs-simulator/task-stages` `/wcs-simulator/history` | TaskStages | 任务看板（SignalR 实时） |
| `/wcs-simulator/grcs-api-docs` | GrcsApiDocs | GRCS 接口说明 |
| `/wcs-simulator/exception-records` | ExceptionRecords | 异常记录台账（按项目隔离） |
| `/wcs-simulator/project-logs` | ProjectLogs | 项目记录台账（按天分组、导出） |

## 数据库表

`AutomationDb.Init()` 自动建表 + 迁移：`kv`、`task_records`、`workflow_state`、`task_templates`、`feature_modules`、`auto_templates`、`mock_rules`、`exception_records`、`project_logs`、`mock_request_events`、`module_exec_logs`

## 核心业务约定（改动时务必遵守）

1. **复现两联动**：异常记录「复现」按钮 = 复现次数 +1、复现时间 = 当前，**不修改状态**（后端 `ExceptionRecordReproduce` 与前端乐观更新均按此实现）。
2. **项目隔离**：异常记录/项目记录均按 `project` 字段（TEXT）隔离数据；GET 必须带 `project=` 参数（空串 = 仅未分类）；下拉项目来自 `GET /projects`（DISTINCT），「未分类」不作为可选项目；当前项目记 localStorage。
3. **删除密码**：删除单条记录与删除项目都需密码 `wayzim`，前后端双校验（后端返回 403「删除密码错误」，前端弹窗先本地校验）。删除项目后成功提示须显示**被删除的项目名**（先存变量再切换）。
4. **日期时间格式**：后端 JSON 序列化 `yyyy-MM-dd HH:mm:ss.fff`（GRCS 解析依赖）；复现时间为 `yyyy-MM-dd HH:mm:ss`。
5. **CORS + SignalR**：`SetIsOriginAllowed(_ => true)` + `AllowCredentials()`（不能用 `AllowAnyOrigin`，否则 SignalR 凭证请求被拦）。
6. **DI 生命周期**：Blazor WASM 中 AddScoped 每标签页单例；依赖 Scoped 的服务必须注册 AddScoped（曾因误注册 Singleton 导致白屏）。
7. **Razor 编码陷阱**：HTML 属性内嵌 C# 字符串双引号会截断；`&quot;` 在 `@()` 内报错；多选 checkbox 用静态数组循环渲染；`<input type="date">` 用 `value` + `@onchange`（勿用 `@bind`）。
8. **弹窗风格**：所有确认/密码/提示对话框统一系统风格（`tpl-modal` 深色样式），不用浏览器原生 `confirm`/`prompt`。
9. **导出 Excel**：第一行醒目标题（`{项目名}_异常记录/项目记录`，合并单元格），第二行表头按**实际列数**涂背景（`Range(2,1,2,headers.Length)`，勿用整行 `headerRow.Style`）；无数据导出时弹窗提醒（不静默）。
10. **导航**：WCS 前端无项目层，两层导航 = 功能 → 页面。