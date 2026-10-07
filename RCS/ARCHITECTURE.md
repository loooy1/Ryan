# RCS 模块边界

RCS 当前按单进程模块化运行。后端负责任务业务与车辆命令，算法负责纯位置路径及多车资源锁，协议适配器负责具体车型报文，前端只调用 RCS API 和实时事件。

| 目录 | 职责 |
| --- | --- |
| `Backend/Modules/Rcs/Api` | HTTP 参数与状态码映射，不直接承载库存事务或地图发布流程 |
| `Application/Tasks/Acceptance` | 上游与手动任务校验、地图连通性检查、库存预约、任务入库 |
| `Application/Tasks/Lifecycle` | 活动任务、状态变更、取消、恢复及执行阶段落库 |
| `Application/Tasks/Queries` | 任务列表、分页与详情的只读查询 |
| `Application/Scheduling` | 自动车辆选择、任务领取及后台调度循环 |
| `Application/Execution/Routes` | 路径转换、滚动窗口、交通锁、续发与重规划 |
| `Application/Execution/Actions` | 站点规则、车辆动作确认与库存完成提交 |
| `Application/Inventory` | 货物与托盘模型、实例、预约和取放货转移 |
| `Application/Maps` | 当前地图快照及保存后的发布刷新 |
| `Protocol` | 车型适配器与车辆会话；适配器只接收车辆配置，不依赖数据库实体 |
| `Infrastructure` | 数据库实体、配置和存储实现 |
| `Algorithms` | A* 路径和多车交通资源；只接收地图、位置、目标和点编码 |

任务接收先同步校验与预约库存，然后入库并返回；自动任务由调度循环领取，手动任务交给指定车辆。执行服务依次处理叫车、起点动作、运送和终点动作。路径窗口只有在车辆确认接收后才提交锁；车辆报告经过站点后释放身后的点和线。取放货动作必须等待车辆完成确认，再更新库存。

地图保存经 `RcsMapPublicationService` 写库并刷新当前快照。运行中的规划持有创建时的地图快照引用。当前交通锁与库存预约是进程内状态，因此部署多个 RCS 实例之前，需要设计单一状态所有者或共享一致性机制。

回归检查：`dotnet test RCS/Tests/Rcs.Architecture.Tests.csproj`。测试覆盖算法路径到车辆协议窗口的转换、已通过点与线的锁释放，以及库存错误 HTTP 状态映射。
