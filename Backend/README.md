# GRCS WCS 模拟器（后端）

WCS 后端（管理面）：对 GRCS 核心系统暴露 WCS 协议回调接口（`/api/v1/*`），对前端提供控制台查询/管理接口（`/api/wcs/*`）。监听端口由 appsettings.json 的 `Urls` 决定（默认 http://0.0.0.0:8230）。GRCS 核心后端（8224）是另一个系统，不在本仓库。

配套前端：`Ryan_DashBoard`（Blazor WASM，模块目录 `Modules/WcsSimulator`）。

---

# 大厂标准 WCS 设计（本项目的架构基准）

## 一、系统定位（物流软件分层）

```
WMS   上层业务：订单/库存/波次/出库计划
WCS   中间控制：任务管理·调度·交通管制·流程编排·设备通信
ECS/RCS 底层：设备控制系统（AGV 群控、输送机、堆垛机、PLC/SCADA）
```

WCS 的职责边界：**管"任务怎么跑完"，不管"业务为什么跑"**（那是 WMS）。所以 WCS 核心是任务生命周期 + 资源（设备/路径/站点）调度 + 流程编排。

## 二、标准模块划分（大厂共性）

| 模块 | 职责 |
|---|---|
| 任务管理 | Task 全生命周期状态机（创建→排队→分配→执行→完成/异常→归档），显式状态迁移表 |
| 调度引擎 | 任务-设备匹配：策略可插拔（最短路径/负载均衡/优先级/时间窗） |
| 路径与交通管制 | 地图图建模、区段占用、十字路口互斥、死锁检测与解除 |
| 设备接入层 | 统一设备抽象 + 协议适配（MQTT/gRPC/Modbus/OPC-UA），心跳、注册中心、命令幂等 + 超时重试 |
| 作业编排 | 作业单→子任务拆解、流程模板可配置、前置/后置条件 |
| 事件总线 | 设备上报→事件→驱动任务流转（事件驱动而非轮询） |
| 数据层 | 关系库（台账）+ 缓存（Redis：地图/库存快照/锁）+ 时序库（设备点位） |
| 监控运维 | 实时大屏、告警中心（规则/阈值）、结构化日志（traceId 贯穿）、配置中心 |
| 集成层 | WMS 接口 + RCS/设备接口，统一 API 网关 |

## 三、关键技术决策（大厂标准答案）

1. **状态机显式建模**：Task/Device 各一张状态迁移表，非法迁移直接拒绝
2. **事件驱动**：设备上报事件 → 总线 → 任务状态机推进；轮询只用于兜底/心跳；进程内单体用事件总线，跨服务才引入消息队列（Kafka/RabbitMQ）
3. **架构形态**：现场项目主流是**模块化单体起步**，按域切服务（任务/调度/设备/库存）是量级到了才拆——不建议直接微服务化
4. **DDD 落地**：实体（任务/设备/作业单）+ 仓储 + 领域服务；聚合根 = 任务
5. **数据分层**：MySQL/PG 存业务 + Redis 存热数据（地图/锁/库存快照）+ 时序库存点位——SQLite 只是量级不够时的替代
6. **可配置化**：地图/站点/路径/规则全部数据化配置，不硬编码；任务类型模板化（前端可配）
7. **可观测性**：结构化日志（traceId/deviceId/taskId 贯穿）、告警规则、Metrics

## 四、本项目对照现状

| 标准模块 | 本项目状态 |
|---|---|
| 任务管理 | ✅ 有 task_records + stage 字段；❌ 无状态机约束（字符串自由流转，待补显式迁移表） |
| 调度引擎 | ❌ 无策略抽象，选点逻辑写死在 runner 里 |
| 路径与交通管制 | ❌ 无（模拟器不涉及真实交通） |
| 设备接入层 | ⚠️ 直连 GRCS HTTP，无设备抽象/心跳/命令幂等 |
| 作业编排 | ✅ auto_templates 可配模板，接近标准 |
| 事件总线 | ❌ 轮询为主，仅少量 SignalR（TaskStageRealtimeHub） |
| 数据层 | ⚠️ 单 SQLite 手写 SQL（见下方 EF Core 规划） |
| 监控运维 | ⚠️ 日志有分组但非结构化，无告警中心 |
| 集成层 | ✅ /api/v1 回调已有雏形 |

**结论**：本项目的形态（模块化单体 + 模板配置 + 台账 + 项目隔离）是标准起点，方向正确；与"大厂"的主要差距是状态机显式化、事件驱动化、设备抽象层、结构化日志——这些是系统变大才痛的点，模拟器阶段不强制。

## 五、数据访问技术决策

- **EF Core**：已定方向（net10 + Microsoft.EntityFrameworkCore.Sqlite 10.0.9 + dotnet-ef 工具 10.0.11）。现有 `grcs.db` 有数据且被 Content 打包，**表结构/存量数据不变，只换访问方式**：表名/列名均为下划线（`task_records`、`happened_at`），映射用 `ToTable()/HasColumnName()` 显式对齐；时间列存 `yyyy-MM-dd HH:mm:ss`（与 GRCS 全局格式一致）。
- **EF 结构约定（模块化单体）**：
  - 实体类放 `Modules/<域>/Infrastructure/Entities/`，映射配置类放 `.../Configurations/`（每表一个 `IEntityTypeConfiguration<T>`），各自模块目录归位（RCS 将来同理）
  - `GrcsDbContext`（`Modules/Shared/Infrastructure/`）单文件，`OnModelCreating` 里 `ApplyConfigurationsFromAssembly` 反射扫描全程序集——**新增业务表零注册、DbContext 零改动**
  - DbContext **不声明 DbSet 属性**，调用方用 `context.Set<T>()`（保持基建不依赖任何模块类型）
  - 生命周期：`AddDbContextFactory` 短生命周期模式（Singleton Store 注入 factory，每次操作 `CreateDbContext()`）
- **迁移流程**（仅改表结构时用，日常读写走 LINQ 无需命令）：
  1. 停掉运行中的后端（exe 被锁则构建失败）
  2. `dotnet ef migrations add <名称>` → `dotnet ef database update`
  3. 接管现有表时实体配置加 `ExcludeFromMigrations()` 防止重建冲突；迁移基线已建（Test 示例表 + `__EFMigrationsHistory`）
- **迁移节奏**：按 Store 逐个渐进迁移（先独立 CRUD 表，后 task_records/kv），Store 公开方法签名不变 → 上层零改动。
- **MediatR**：**明确不引入**。本项目为瘦控制器 → 直调 Store/Service 结构，无领域事件/多 handler 广播/复杂编排需求；MediatR 对应大厂事件总线位置，待真正出现"一个事件多个消费者"或"跨服务编排"需求时再评估。
- 模块化约定：每个业务域一个 `Modules/<域>/` 目录（Controllers/Models/Services），通过 `Modules/<域>/XxxModuleExtensions.AddXxxModule()` 在 `Program.cs` 挂接注册。

## 六、构建与运行

```powershell
dotnet build GrcsBackend.csproj -v q --nologo
dotnet run
```

依赖：Microsoft.AspNetCore.Mvc.NewtonsoftJson 10.0.9（GRCS 按 `yyyy-MM-dd HH:mm:ss.fff` 解析响应，序列化格式必须对齐）、Microsoft.Data.Sqlite 10.0.9（纯托管 + 预编译 bundle，免装）。