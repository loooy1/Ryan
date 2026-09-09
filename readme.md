# GRCS WCS 管理系统

WCS（仓库控制系统）管理面：`WCSBackend`（.NET 后端）+ `Dashboard`（Blazor WASM 前端）+ 共享层（`Contracts` 契约库 + `Backend.Shared` 设施库）。未来新增独立系统 `RCSBackend`（替代 GRCS 的核心系统，骨架已建）。

- **WCS 后端**对 **GRCS 核心系统**（8224 端口）暴露 WCS 协议回调接口 `/api/v1/*`，对前端提供管理接口 `/api/wcs/*`。
- **前端是"薄遥控壳"**：执行权（自动化模板、信号自动放行、模块执行、移动循环）全部收归后端，前端只做遥控 + 展示 + 状态同步（SignalR 实时推送）。
- **RCS 后端**（规划）：实现 GRCS 协议服务端（`/api/Cargo`、`/api/Map/GetMap`、`/api/RawOrder/ChangeFloor`、`/api/v1/task_receive` 等），WCS 只改 GRCS 地址配置即可切换对接。

## 项目结构

```
Running_code/
├── Contracts/        # 前后端共用契约库：DTO（传输）/ Entity（持久化），纯 POCO，无包依赖
├── Shared/           # 后端共享设施库（Backend.Shared）：DbContext / 仓储 / WAL / 管线扩展
├── WCSBackend/       # WCS 程序（.NET 10 + EF Core + SQLite WCS.db + SignalR），端口 8230
├── RCSBackend/       # RCS 程序（骨架，端口 8231 / 独立 rcs.db）
├── DashBoard/        # Dashboard：Blazor WebAssembly + MudBlazor，命名空间 Dashboard.*
├── Solutions/        # Ryan.sln 解决方案（另 RcsBackend 自带独立 sln）
└── 数据库备份/        # SQLite 数据库备份
```

架构约定：**WCS/RCS 共用 `Contracts` + `Backend.Shared`，其余各自独立**（端口、数据库文件、EF 迁移、业务模块均隔离）。

## 后端分层

### 共享层（Backend.Shared，命名空间 `Backend.Shared.*`）

- `GrcsDbContext`：统一 EF 数据上下文，`OnModelCreating` 按**传入的配置程序集**逐个 `ApplyConfigurationsFromAssembly` 自动发现实体配置（WCS 传 WCSBackend 程序集、RCS 传 RCSBackend 程序集，各扫各的）。
- Repository 全家：IRepository / IUnitOfWork / IUnitOfWorkFactory（IDbContextFactory 短生命周期模式，兼容 Singleton Store 注入）。
- `SharedModuleExtensions.AddSharedModule(dbFileName, migrationsAssembly, params configAssemblies)`：连接串指向 `ContentRoot/{dbFileName}`（WCS.db / rcs.db），显式指定 **MigrationsAssembly**（DbContext 在共享库，否则 EF 找不到各应用的迁移）。
- `WebPipelineExtensions`：AddGrcsJson（NewtonsoftJson `yyyy-MM-dd HH:mm:ss.fff`，GRCS 协议硬要求）/ AddGrcsCors（CORS_ORIGIN）/ UseGlobalErrorHandler / MapGrcsHealthCheck。
- `SqliteWal.EnsureWal`：WAL 模式（读写互不阻塞，写冲突排队 DefaultTimeout=30）。

### WCSBackend（`WCSBackend.Modules.*`）

```
Modules/
├── Wcs/               # WCS 总模块
│   ├── Automation/    # 自动化执行引擎（AutoTemplateRunner/TaskDispatcher/ModuleRunService/
│   │                  #   SignalAutoHostedService/MoveLoopRunner/NestRunner/InventoryCoordinator/TemplateValidator）
│   ├── Proxy/         # GRCS 对接层（GrcsHttpClient：5 秒在线熔断；GrcsProxyController 代理）
│   ├── Console/       # 控制台接口层（全部 /api/wcs/* 控制器）
│   ├── Realtime/      # SignalR Hub（任务事件/统计/审批/模块日志）
│   └── Infrastructure/ # Store 层 + EF 配置（12 个实体配置自动发现）
└── Migrations/         # WCS 自己的 EF 迁移（启动时自动 Migrate）
```

### RCSBackend（骨架，`RCSBackend.Modules.Rcs.*`）

```
Modules/Rcs/
├── RcsModuleExtensions.cs   # AddRcsModule() 空骨架
├── Protocol/                # GRCS 协议服务端（业务定义后添加）
├── Console/                 # /api/rcs/* 管理接口（业务定义后添加）
├── Realtime/                # RCS Hub（业务定义后添加）
└── Infrastructure/          # RCS 实体 + EF 配置 + 迁移（业务定义后添加）
```

### 数据访问约定

- 所有 Store 均为 **Singleton**，通过 `IDbContextFactory` 短生命周期模式访问数据库（每次操作 `CreateDbContext`）。
- SQLite 并发防护三层：**WAL 模式**（读写互不阻塞）+ **busy_timeout=30s**（写冲突排队等待）+ **应用层 WriteLock**（进程内串行化写）。
- 库存唯一事实源：`wcs_inventory` 账本（`WcsInventoryStore`），自动化只做状态流转（idle→picked→busy→idle），**拉取 GRCS 库存写库只在手动「同步库存」时发生**。
- 迁移约定：WCS/RCS 各自 `Add-Migration`（迁移快照按各自程序集配置生成，互不干扰）。

## 前端分层

```
DashBoard/
├── Layout/              # MainLayout（两列导航）+ 状态栏 + 连接告警
├── Pages/               # Home / NotFound
└── Modules/WcsSimulator/
    ├── Components/      # PageBase（统一反馈提示）+ PageStateBase（折叠持久化/localStorage 辅助）
    ├── Pages/           # 9 个页面：任务下发/自动化/信号交互/异常台账/项目日志/任务看板/地图/库存/接口文档
    ├── Services/        # 10 个服务
    │   ├── WcsApiClient.cs       # 后端 API 客户端（遥控壳核心，含连接守卫/统一告警）
    │   ├── AutomationHub.cs      # 自动化共享轮询中枢（1s 拉快照/日志/范围）
    │   ├── TaskStageHub.cs       # SignalR 共享缓存（8 类推送，唯一实时数据源）
    │   ├── MapParseService.cs    # 地图解析（map.json → 精简站点，纯数据转换）
    │   └── BackendHealthService.cs  # WCS/GRCS 在线状态（单数据源）
    └── Models/           # 地图解析/任务类型/功能模块注册表
```

技术栈：Blazor WebAssembly + MudBlazor + 手写 CSS + SignalR（JS 桥）+ localStorage 缓存 + ClosedXML（Excel 导出）。DI 全部 `AddScoped`（WASM 中 Scoped ≈ 每标签页单例）。

## 健壮性设计

- SQLite 并发三层防护（WAL + busy_timeout + WriteLock）
- GRCS 在线熔断：5 秒超时判离线，离线自动跳过真实请求
- 互斥闸 `AutomationGate`：模板轮询 / 单次执行 / 移动循环三者串行（进程内）
- 信号幂等 `SignalConfirmStore`：抢占式插入防多标签页重复发信号
- 全局异常中间件：统一 `{"error":"..."}` 响应
- 健康检查 `/health/ready` + 前端连接告警自动恢复
- 日志体系（`AutomationLogService`）：按轮次分组的内存日志（**文件日志已移除，待重新设计**）

## 已知缺口 / 待办（暂不实施）

按优先级排列，后续需要时再处理：

1. **P0 补测试**：`Repository.FindAsync` 主键探测、`BuildPool` 分类、`TemplateValidator`、`TaskDispatcher` 选点/报文组装、`StationTypeHelper` 位解码均无测试。
2. **P0 统一异步与异常**：大量 fire-and-forget（`_ = PollLoop(...)` 等）无统一任务跟踪；多处 `catch { }` 静默吞噬，故障无痕迹。
3. **P0 GRCS 调用重试**：`AddHttpClient` 已注册但未挂 Polly 重试/指数退避。
4. **P1 拆分上帝类**：`Stores.cs`（437 行，聚合 10 个 Store）、`TemplateStores.cs`（407 行，聚合 4 个模板 Store）→ 独立文件。
5. **P1 实体与 DTO 解耦**：`ExceptionRecordDto`/`ProjectLogDto` 兼作表行，字段演进互相牵制。
6. **P1 `Repository.FindAsync` 去反射**：按实体显式注册主键类型。
7. **P2 数据归档**：`task_records` 10000 条上限，超限后归档/导出方案。
8. **P2 多实例考量**：`AutomationGate`/`WriteLock` 为进程内锁，水平扩展需分布式锁。
9. **P3 前端收敛**：三套样式体系（MudBlazor + 手写 CSS + Bootstrap）并存；WASM 体积优化（懒加载）。
10. **P2 RCS 协议基线**：RCS 开工前以 `GrcsHttpClient.cs` + `GrcsApiDocsController.cs` 为协议清单，确认 GRCS 接口兼容范围。