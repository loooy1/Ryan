# GRCS WCS 管理系统

WCS（仓库控制系统）管理面：`Backend`（.NET 后端）+ `DashBoard`（Blazor WASM 前端）+ `Contracts`（共享契约库）。

- 后端对 **GRCS 核心系统**（8224 端口）暴露 WCS 协议回调接口 `/api/v1/*`，对前端提供管理接口 `/api/wcs/*`。
- 前端是"薄遥控壳"：执行权（自动化模板、信号自动放行、模块执行、移动循环）全部收归后端，前端只做遥控 + 展示 + 状态同步（SignalR 实时推送）。

## 项目结构

```
Running_code/
├── Backend/        # GrcsBackend：.NET 8 + EF Core + SQLite（grcs.db）+ SignalR
├── DashBoard/      # GRCS.Dashboard：Blazor WebAssembly + MudBlazor
├── Contracts/      # GrcsBackend.Contracts：DTO（传输）/ Entity（持久化）共享库
├── Solutions/      # Ryan.sln 解决方案
└── 数据库备份/      # SQLite 数据库备份
```

## 后端分层

模块化单体：`Modules/<域>/` 目录 + `XxxModuleExtensions` 挂接 DI 注册，新增模块零侵入。

```
Modules/
├── Shared/            # 共享基础设施
│   └── Infrastructure/
│       ├── GrcsDbContext.cs      # 统一 DbContext：ApplyConfigurationsFromAssembly 自动发现实体配置
│       └── Repository/           # 工作单元 + 泛型仓储（IDbContextFactory 短生命周期模式）
├── Wcs/               # WCS 总模块
│   ├── Automation/    # 自动化执行引擎
│   │   ├── AutoTemplateRunner.cs    # 模板执行引擎（轮询/单次模式，IHostedService）
│   │   ├── TaskDispatcher.cs        # 任务下发器：选点+加锁+组装+下发+释放
│   │   ├── ModuleRunService.cs      # 模块执行引擎（起点前/起点后/终点三类时机）
│   │   ├── SignalAutoHostedService.cs  # 信号自动放行（3s 轮询）
│   │   ├── MoveLoopRunner.cs        # 纯移动任务循环
│   │   ├── NestRunner.cs            # 归巢模式
│   │   ├── InventoryCoordinator.cs  # 库存账本协调：选池/统计/占用
│   │   └── TemplateValidator.cs     # 模板保存前校验
│   ├── Proxy/          # GRCS 对接层（GrcsHttpClient：5 秒在线熔断）
│   ├── Console/        # 控制台接口层（全部 /api/wcs/* 控制器）
│   ├── Realtime/       # SignalR Hub（任务事件/统计/审批/模块日志）
│   └── Infrastructure/ # Store 层 + EF 配置（12 个实体配置自动发现）
└── Migrations/         # EF Core 迁移（启动时自动 Migrate）
```

### 数据访问约定

- 所有 Store 均为 **Singleton**，通过 `IDbContextFactory` 短生命周期模式访问数据库（每次操作 `CreateDbContext`）。
- SQLite 并发防护三层：**WAL 模式**（读写互不阻塞）+ **busy_timeout=30s**（写冲突排队等待）+ **应用层 WriteLock**（进程内串行化写）。
- 库存唯一事实源：`wcs_inventory` 账本（`WcsInventoryStore`），自动化只做状态流转（idle→picked→busy→idle），**拉取 GRCS 库存写库只在手动「同步库存」时发生**。

## 前端分层

```
DashBoard/
├── Layout/              # MainLayout（两列导航）+ 状态栏 + 连接告警
├── Pages/               # Home / NotFound
└── Modules/WcsSimulator/
    ├── Components/      # PageBase（统一反馈提示）
    ├── Pages/           # 9 个页面：任务下发/自动化/信号交互/异常台账/项目日志/任务看板/地图/库存/接口文档
    ├── Services/        # 10 个服务
    │   ├── WcsApiClient.cs       # 后端 API 客户端（遥控壳核心）
    │   ├── AutomationHub.cs      # 自动化共享轮询中枢（1s 拉快照/日志/范围）
    │   ├── TaskStageHub.cs       # SignalR 共享缓存（8 类推送，唯一实时数据源）
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