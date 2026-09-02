# WCS 模拟器（WCS 管理面 + 前端模拟器）

GRCS 外围系统的 WCS 部分：后端（管理面）对 GRCS 核心系统暴露 WCS 协议回调接口，对前端提供控制台查询/管理接口；前端（Blazor WASM 模拟器）模拟 WCS 的各类作业/信号交互，同时提供台账与监控页面。

```
F:\A_Code\Running_code\
├─ Ryan_Backend\      后端（ASP.NET Core Web API，net10.0，端口 8230）
└─ Ryan_DashBoard\    前端（Blazor WebAssembly + MudBlazor，net10.0）
```

> GRCS 核心后端（8224）是另一个系统，不在本仓库。

---

## 一、后端 Ryan_Backend（GrcsBackend）

### 技术栈

| 项 | 内容 |
|---|---|
| 框架 | .NET 10（`net10.0`），ASP.NET Core Web API（`Microsoft.NET.Sdk.Web`） |
| 序列化 | Newtonsoft.Json（`Microsoft.AspNetCore.Mvc.NewtonsoftJson` 10.0.9），`DateFormatString = "yyyy-MM-dd HH:mm:ss.fff"`（必须与 GRCS 解析格式对齐） |
| 数据库 | SQLite（`Microsoft.Data.Sqlite` 10.0.9，纯托管 + 预编译 bundle，免安装）；库文件 `grcs.db`（随编译输出复制） |
| 实时推送 | SignalR（`/hubs/task-stages`） |
| HTTP 出站 | `IHttpClientFactory`（`GrcsHttpClient` 调用 GRCS 8224） |
| 多标签页互斥 | `AutomationGate`（轮询/批量互斥闸，全后端只有一个执行者） |

### 运行

- 端口：`appsettings.json` → `Urls: http://0.0.0.0:8230`
- 命令（workdir = `Ryan_Backend`）：

```powershell
dotnet build GrcsBackend.csproj -v q --nologo -o C:\Users\wayzim\AppData\Local\Temp\opencode\grcs_build
dotnet run
```

> 注意：SQLite 表结构变更/新接口上线后需**重启后端**生效；前端可硬刷新（Ctrl+F5）。

### 模块结构（`Modules/Wcs/`）

```
Modules/Wcs/
├─ WcsModuleExtensions.cs     DI 注册入口（Program.cs 只调 AddWcsModule()）
├─ Proxy/                     对 GRCS 的代理/转发（GrcsProxyController、GrcsHttpClient）
├─ Automation/                自动下发引擎：模板执行 AutoTemplateRunner、纯移动循环 MoveLoopRunner、
│                             归巢 NestRunner、统一模块执行 ModuleRunService、终点执行器
│                             FinishedModuleWatcher、信号自动放行 SignalAutoHostedService
├─ Console/                   控制台查询/管理：Controllers + Services + Models
├─ Realtime/                  SignalR（TaskStageRealtimeHub，任务阶段事件实时推送）
└─ Infrastructure/            SQLite 数据层（AutomationDb）+ 各 Store + 日志服务
```

关键注册（`WcsModuleExtensions.cs`）：`AutomationDb`/各 `Store`/`GrcsHttpClient`/`GrcsInventoryCacheService`（2 秒库存轮询缓存，HostedService）均为 Singleton；`AutoTemplateRunner`、`SignalAutoHostedService`、`FinishedModuleWatcher` 为 `IHostedService` 常驻服务；HostedService 需同时以 Singleton 注册供控制器注入。

### API 一览

| 前缀 | 用途 |
|---|---|
| `api/wcs` | WCS 控制台总入口（`WcsConsoleController`）+ 通用转发（`ForwardController`） |
| `api/wcs/auto` | 自动化：状态快照/日志/执行（`AutomationConsoleController`） |
| `api/wcs/grcs` | 对 GRCS 的代理调用（`GrcsProxyController`） |
| `api/wcs/exception-records` | 异常记录台账（含 `GET /projects`、`DELETE projects/{name}?password=`、`POST /{id}/reproduce`） |
| `api/wcs/project-logs` | 项目记录台账（每日日程，同上项目隔离/删除密码） |
| `api/wcs/ledger` / `api/wcs/map` / `api/wcs/templates` / `api/wcs/modules` | 台账、地图缓存、任务模板/功能模块 |
| `api/wcs/signal-confirm` | 信号确认（`SignalConfirmController`） |
| `api/wcs/mocks` + `api/{*path}`（Order 999） | Mock 入站：`api/v1/*` 之外的任意路径兜底模拟 |
| `api/wcs/grcs-api-docs` | GRCS 接口说明清单（内置静态 + 数据库动态） |
| `hubs/task-stages` | SignalR：任务阶段事件实时推送 |

### 数据库表（`AutomationDb.Init()` 自动建表 + 迁移）

`kv`、`task_records`、`workflow_state`、`task_templates`、`feature_modules`、`auto_templates`、`mock_rules`、`exception_records`、`project_logs`、`mock_request_events`、`module_exec_logs`

---

## 二、前端 Ryan_DashBoard（GRCS.Dashboard）

### 技术栈

| 项 | 内容 |
|---|---|
| 框架 | .NET 10（`net10.0`），Blazor WebAssembly（`Microsoft.NET.Sdk.BlazorWebAssembly`） |
| UI | MudBlazor 8.15.0 |
| Excel 导出 | ClosedXML 0.105.1 |
| 实时 | SignalR 客户端（`TaskStageHub`，任务看板事件推送，前端不再轮询 task-stages） |
| 持久化 | 浏览器 localStorage（`LocalStoreService`，如当前项目记忆 `grcs_er_project`/`grcs_pl_project`） |

### 运行

```powershell
dotnet build -v q --nologo        # workdir = Ryan_DashBoard，0 错误为通过
dotnet run
```

### 结构

```
Ryan_DashBoard/
├─ Layout/                   MainLayout（两层侧边栏导航）、BackendStatus（后端在线绿点）、AutomationStatusBar
├─ Services/                 通用服务（ModuleNavigationService、BackendHealthService 已移入 WCS 模块）
├─ Modules/WcsSimulator/     WCS 模拟器模块
│   ├─ Pages/                9 个页面（见下表）
│   ├─ Services/             WcsApiClient（后端遥控壳）、AutomationHub（1s 轮询中枢）、
│   │                        各瘦壳服务、BackendHealthService、ModuleNavigationService
│   ├─ Models/               DTO 与模型（BackendDtos.cs 集中后端 DTO）
│   └─ Extensions/           扩展
└─ _Imports.razor            全局 using 清单（新增模块在此补充命名空间）
```

### 页面路由

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

导航为两层结构：点「WCS模拟器」直接进入 8 个页面导航（`ModuleNavigationService.SelectFeature`）；异常记录/项目记录为 `Pinned` 项，固定在侧边栏底部状态区上方。

### 关键服务

- **WcsApiClient**：后端 HTTP 遥控壳（10s 超时），所有 `/api/wcs/*` 调用入口
- **AutomationHub**：1s 轮询中枢（状态快照 + 增量日志 + 后端健康探测），各页面共享；同时回报状态给 BackendHealthService
- **BackendHealthService**：后端存活状态单一数据源（WCS 8230 + GRCS 8224），BackendStatus 渲染、页面连接判定
- **TaskStageHub**：任务阶段 SignalR 客户端
- **MoveLoopService / SignalAutoService / TaskLedgerService**：纯移动循环 / 信号自动放行 / 台账瘦壳
- **LocalStoreService**：localStorage 封装

### 打包（build-installer.ps1）

发布 Blazor WASM 静态站 → 自包含单文件 launcher（win-x64）→ 合并 wwwroot 与静态资源清单 → 生成 Inno Setup 安装包（需安装 Inno Setup 6）。

---

## 三、核心业务约定（改动时务必遵守）

1. **复现两联动**：异常记录「复现」按钮 = 复现次数 +1、复现时间 = 当前，**不修改状态**（用户明确要求；后端 `ExceptionRecordReproduce` 与前端乐观更新均按此实现）。
2. **项目隔离**：异常记录/项目记录均按 `project` 字段（TEXT）隔离数据；GET 必须带 `project=` 参数（空串 = 仅未分类）；下拉项目来自 `GET /projects`（DISTINCT），「未分类」不作为可选项目；当前项目记 localStorage。
3. **删除密码**：删除单条记录与删除项目都需密码 `wayzim`，前后端双校验（后端返回 403「删除密码错误」，前端弹窗先本地校验）。删除项目后成功提示须显示**被删除的项目名**（先存变量再切换）。
4. **日期时间格式**：后端 JSON 序列化 `yyyy-MM-dd HH:mm:ss.fff`（GRCS 解析依赖）；复现时间为 `yyyy-MM-dd HH:mm:ss`。
5. **CORS + SignalR**：`SetIsOriginAllowed(_ => true)` + `AllowCredentials()`（不能用 `AllowAnyOrigin`，否则 SignalR 凭证请求被拦）。
6. **DI 生命周期**：Blazor WASM 中 AddScoped 每标签页单例；依赖 Scoped 的服务必须注册 AddScoped（曾因误注册 Singleton 导致白屏）。
7. **Razor 编码陷阱**：HTML 属性内嵌 C# 字符串双引号会截断；`&quot;` 在 `@()` 内报错；多选 checkbox 用静态数组循环渲染；`<input type="date">` 用 `value` + `@onchange`（勿用 `@bind`）。
8. **弹窗风格**：所有确认/密码/提示对话框统一系统风格（`tpl-modal` 深色样式），不用浏览器原生 `confirm`/`prompt`。
9. **导出 Excel**：第一行醒目标题（`{项目名}_异常记录/项目记录`，合并单元格），第二行表头按**实际列数**涂背景（`Range(2,1,2,headers.Length)`，勿用整行 `headerRow.Style`）；无数据导出时弹窗提醒（不静默）。
10. **导航**：WCS 前端无项目层（已移除「泰国TWD项目」），两层导航 = 功能 → 页面。