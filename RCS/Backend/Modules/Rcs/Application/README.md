# RCS 应用层

当前按任务服务、调度器、执行服务划分职责。它们通过 C# interface 在 RCS 后端同一进程内协作，不经过 HTTP；对外 HTTP 接口位于 `../Api`。

```text
Application/
├─ Tasks/                         接收、查询、任务状态与持久化协调
│  ├─ IRcsTaskService.cs           供 API、模拟页面入口和后台服务调用
│  ├─ RcsTaskService.cs
│  ├─ IRcsTaskStore.cs             数据存储接口，实现位于 Infrastructure/Stores
│  ├─ RcsTaskRequestValidator.cs   报文格式校验
│  └─ RcsTaskConflictException.cs  重复 TaskId 的内容冲突
├─ Vehicles/                      车辆定义、实例缓存与管理操作
│  ├─ IRcsVehicleStore.cs          车辆配置存储接口
│  ├─ RcsVehicleRegistry.cs        按车辆编号创建实例并提供状态
│  └─ RcsVehicleService.cs         创建、配置、删除及初始位置保存
├─ Scheduling/                    选择任务与车辆，输出分配结果
│  ├─ IRcsTaskScheduler.cs         调度策略接口
│  ├─ RcsTaskScheduler.cs          当前优先级/FIFO 策略
│  ├─ RcsTaskCandidate.cs          等待任务摘要
│  ├─ RcsTaskAssignment.cs         TaskId、QueueId、VehicleId 分配结果
│  ├─ RcsDispatchLock.cs           协调任务领取、车辆管理和控制
│  └─ RcsTaskDispatchWorker.cs     随宿主运行的后台循环
├─ Execution/                     路径规划和车辆运行
│  ├─ IRcsTaskExecutionService.cs  规划、执行、车辆状态和控制接口
│  ├─ RcsTaskExecutionService.cs
│  ├─ RcsTaskExecutionRequest.cs   指定任务、车辆、地图及有序站点
│  ├─ RcsTaskRoutePlanner.cs       业务站点动作及算法节点转换为协议点
│  └─ RcsTaskExecutionPlan.cs      规划结果及同一地图快照引用
└─ Simulation/                    地图展示、路径预览和旧移动入口的适配
   └─ RcsSimulationService.cs
```

## 调用过程

1. 上游 API 通过 `IRcsTaskService.ReceiveAsync` 接收、校验、保存任务，并唤醒后台调度器；调度器只向 AUTO 车辆分配。
2. RCS 界面手动任务通过 `ReceiveManualAsync` 校验 MANUAL 目标车空闲后直接绑定并启动执行，不进入调度器队列。
3. `RcsTaskDispatchWorker` 通过任务服务触发一次自动任务分配。任务服务从存储读取 UPSTREAM 等待任务摘要和车辆状态。
4. `IRcsTaskScheduler.Select` 接收任务摘要及车辆状态，只返回 AUTO 车辆的分配结果。当前 PriorityCode 越小越优先，同优先级按 QueueId；车辆需要启用、Idle 或 Arrived。
5. 任务服务将自动分配结果保存为 RUNNING。只有成功领取的任务才能继续执行。
6. `IRcsTaskExecutionService.Prepare` 把车队位置和分配车辆的目标站点交给 `RCS/Algorithms`，生成执行计划。任务服务保存路径和地图版本后，调用 `ExecuteAsync`。
7. 执行结束或异常返回任务服务，由任务服务保存完成、失败、取消或中断状态并发布 TaskChanged，再分配下一任务。

## 职责边界

| 部分 | 负责 | 依赖边界 |
| --- | --- | --- |
| 任务服务 | 接收、幂等、持久化、生命周期、控制命令与调度执行协调 | 依赖存储、调度和执行接口；不直接调用算法或虚拟车。 |
| 调度器 | 挑选等待任务和可用车辆 | 输入输出均为普通模型；不访问 EF、地图缓存、算法或车辆对象。 |
| 执行服务 | 地图与场景检查、向算法同步车辆遥测和目标点、协议下发、暂停/继续/重置及状态事件 | 使用地图缓存、算法交通规划接口和 IVehicleCommandChannel；不访问任务表，也不选择下一任务。 |
| 多车交通规划 | 获取全车当前位置及各车目标点，规划路线、维护点/线锁，冲突时尝试避让并重新规划 | 位于 `RCS/Algorithms`；输入不含任务类型或点位动作，RCS 在协议下发前附加动作。 |

数据库实体、EF 映射和 MySQL 存储继续放在 `Infrastructure`；对外任务报文在 `Shared/Contracts/Rcs/Tasks`，调度与执行的内部参数在所属目录，不扩充公共报文。

取消令牌与任务状态由任务服务管理，执行服务遵守取消令牌。暂停、恢复和重置经任务服务协调持久化后再控制车辆，不能由 HTTP 控制器直接操作 IVirtualVehicle。

## 更换调度策略

新增实现 `IRcsTaskScheduler` 的类，并在 `RcsModuleExtensions` 替换该接口的注册。无需修改任务接收、执行服务或对外接口。例如后续可以扩充等待任务摘要或车辆状态，加入距离、电量、能力等规则。

目前已支持多虚拟车并行。任务服务按车辆编号保存活动任务与取消令牌，执行服务按车辆编号保存运行计划，协议通道按编号找到对应车实例。RCS调度器负责任务分配；执行服务持续把各车位置投影为算法遥测，并登记已分配车辆的目标点。多车交通规划器维护车队上下文及点/线预约，优先为新路线避开其他车辆当前位置和已预约资源；续发冲突时会暂停车辆并尝试重新规划，无法立即获得替代路线时恢复原路线并等待锁。业务动作仍由 RCS 保留。后台循环只领取并启动任务，不等待该车整段运行结束，因此可以继续给其他空闲车分配。每车终态保存后才释放占用；宿主停止时取消并等待所有运行任务结束。

车辆增删和自动任务领取共用 RcsDispatchLock，避免删除时同时被分配任务。手动任务在锁内直接占用选定 MANUAL 车辆并启动，不经过调度策略。车辆管理服务通过任务接口协调重置，并在同一锁范围保存初始站点。调度器只处理 UPSTREAM 等待任务和 AUTO 车辆，不依赖数据库或协议实现。当前交通控制器与 RCS 同进程运行，算法契约已与业务动作解耦；尚未拆成独立网络服务，也未实现时间扩展 MAPF 或跨 RCS 实例共享预约。


## 车辆协议

执行服务通过 `../Protocol/IVehicleCommandChannel` 发送 MOVE、SLIDE_ROUTE、UPDATE_ROUTE、STOP、PAUSE、RESUME、RESET。当前进程内通道连接 IVehicle；任务服务及调度器不直接依赖车辆实现。`RCS/Algorithms` 根据缓存地图、全车位置、已分配车辆的目标站点和系统设置生成路线及路径段，并拥有进程内的点/线预约；算法输入不含具体任务类型、容器或点位动作。RCS执行服务将任务动作附到算法路径，再转成车型协议。每段点数及续发间隔可在 RCS「系统设置 → 算法设置」调整。

重新规划由任务服务协调暂停、调用执行服务更新剩余路径、保存新路径，再恢复原运行任务。原任务标识和 MOVE 完成等待保留，已经完成的站点步骤不重放。协议详情见 [车辆协议](../../../车辆协议.md)。
