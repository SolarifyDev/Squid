# Squid 项目触发器第一版开发规划

**`Trigger-Feature` 是四个来源分支的共同起点。** 公共代码提供配置与运行历史 API、来源处理器契约，以及创建和派发真实 Release、Deployment 和 ServerTask 的链路。你们负责 Deployment target Auto-deploy 和 Scheduled deployment，Git repository 和 External feed 由对应同事接入。

## 四类触发器分别做什么

| 入口 | 什么变化值得处理 | 直接业务动作 |
| --- | --- | --- |
| Git repository | 部署流程引用的仓库出现新提交，匹配该来源的包含或排除路径。支持 glob。Config as Code 项目的流程来源及创建 Release 的项目分支受默认分支约束 | 在选定 Channel 创建 Release，记录发现的仓库、ref 和 SHA。后续自动部署取决于 Lifecycle。[Git 规则](https://octopus.com/docs/projects/project-triggers/git-triggers) |
| Deployment target Auto-deploy | 机器发生所选事件，并匹配环境和 target tags，对应 Squid 的机器 roles | 取该项目、环境、租户当前成功部署使用的 Release 和步骤选择，只向触发事件的机器补部署。可配置是否重部署已是当前版本的机器。[目标触发规则](https://octopus.com/docs/projects/project-triggers/deployment-target-triggers) |
| Scheduled deployment | 某一时区下的计划时刻到达 | 有三种动作。① `DeployLatestRelease` 将来源环境最近成功部署的 Release 部署到目的环境，两端可相同。② `DeployNewRelease` 新建 Release 并部署。③ `DeployLatestReleaseToEnvironment` 将所选 Channel 最新已有的 Release 部署到该 Lifecycle 允许的起始环境，无需来源环境。[定时动作](https://octopus.com/docs/projects/project-triggers/scheduled-deployment-trigger) |
| External feed | 流程引用的包、容器镜像或 Helm Chart 出现比上次 Release 更新、符合 Channel 版本规则的版本。Config as Code 从项目默认分支选择监视依赖 | 在所选 Channel 创建 Release，固定当次解析出的依赖版本。监视对象决定何时触发，整个流程的依赖决定 Release 的内容。后续自动部署归 Lifecycle。[Feed 规则](https://octopus.com/docs/projects/project-triggers/external-feed-triggers) |

例如，Test 最后成功部署 `1.8.0`，Channel 已创建 `1.9.0`。新机器加入 Test 时应补 `1.8.0`。按计划从 Test 晋级到 Prod 也选 `1.8.0`。按计划取 Channel 最新已有版本部署到允许的起始环境，会选 `1.9.0`。因此契约应同时保留 `TriggerType` 和 `ActionType`，并明确每种组合怎样选 Release。Octopus API 也分别表达 Filter 和 Action。内置包库触发器是另一个产品入口。[项目触发器总览](https://octopus.com/docs/projects/project-triggers) · [Project Triggers API](https://octopus.com/docs/api/project-triggers)

## 公共分支交付清单

| 公共能力 | `Trigger-Feature` 提供的实现 | 四个分支怎样使用 |
| --- | --- | --- |
| 项目配置 | `ProjectTrigger` 实体、EF 映射、DbUp 脚本、DataProvider。公共字段为 `Id`、`SpaceId`、`ProjectId`、名称、描述、类型、动作、启停、执行身份、配置修订号和审计字段。Filter/Action 配置用带 `schemaVersion` 的 JSON 保存 | 各分支提供强类型 DTO 和校验器。发布时只开放已注册类型及动作。未注册的组合可查看摘要、停用和软删除。创建、更新或启用该组合时返回明确错误 |
| 管理 API | 沿用薄 Controller → Mediator → Service。提供按项目分页列表、详情、创建、更新、启停、删除、分页运行历史。更新和启停带预期修订号，冲突时提示刷新 | 前端共用列表、编辑外壳、启停和历史详情。类型表单各自交付。当前检出的源码以服务端为主，公共分支先交 API 契约、示例响应和可用类型列表。[现有 Controller 示例](../src/Squid.Api/Controllers/ChannelController.cs#L17) |
| 输入与过滤扩展点 | 按类型注册一个 `IProjectTriggerHandler`，沿用 Autofac 的 `IScopedDependency`。契约涵盖配置校验、将来源事实转为稳定事件键和动作输入、给出跳过原因。接受外部观察结果和到期时刻两种入口 | Git/Feed 分支负责读取来源，Target 分支负责采集机器变化，Scheduled 分支负责算时刻。公共处理器注册表只负责校验与分派。用一个测试处理器率先走通全链 |
| 游标与运行记录 | `ProjectTriggerState` 保存来源游标、上次检查时间/结果、下次到期时间。`ProjectTriggerRun` 保存一次触发的原因、输入、配置修订、执行身份、处理状态及业务对象 ID | 空轮询只更新最近检查。匹配、跳过和失败可解释。历史页可从一次触发追到创建的 Release、Deployment、ServerTask，部署的最终结果读取原任务 |
| 执行与调度 | 一个持久化执行器负责领单、重试、记录原因及恢复。复用静态 Hangfire recurring job 唤醒到期扫描和待派发任务。业务写入复用 `CreateRelease`、`CreateDeployment` 两个操作，`DeployNewRelease` 组合两者 | 各处理器提交已解析的计划，无需各写一套队列、版本分配或部署执行器。评估频率由来源决定。Octopus 的轮询间隔不作为 Squid 接口承诺。[Hangfire 注册](../src/Squid.Api/Extensions/Hangfire/HangfireRegistrar.Squid.cs#L40) |
| 权限与审计 | 服务端从项目及关联实体解析 `SpaceId`，验证 Channel 属于项目、环境及来源属于可访问范围。读取用 `ProjectView`，管理用 `ProjectEdit`。保存可执行配置、启用及实际执行时按动作校验 `ReleaseCreate` 和/或目的环境的 `DeploymentCreate`，涉及来源环境时也校验读取权 | 默认绑定创建者，修改执行参数或重新启用时绑定通过授权的操作者。读来源和提交业务动作前分别复查身份与权限。名称、描述单独变更时保留执行人、游标和待处理 Run。配置变更进入项目审计。[动作及环境授权](../src/Squid.Core/Services/ProjectTriggers/Management/ProjectTriggerAccess.cs#L39) · [后台执行身份](../src/Squid.Core/Services/ProjectTriggers/Runtime/ProjectTriggerExecutionUser.cs) |

### 四个分支共用的调用契约

`/api/project-triggers` 提供 `GET list/detail/history/types` 和 `POST create/update/enable/disable/delete`。所有接口均传 `projectId`，详情和历史按项目及 Space 校验归属。列表返回类型、动作、启停、描述、最近检查及结果和下次到期时间。分页从 1 开始，默认每页 20 条，上限 100 条。删除后可按原 ID 查历史。[项目触发器 Controller](../src/Squid.Api/Controllers/ProjectTriggerController.cs)

| 接口或数据 | 公共输入和输出 |
| --- | --- |
| 类型注册 | `GitRepository → CreateRelease`，`ExternalFeed → CreateRelease`，`DeploymentTarget → AutoDeploy`，`Scheduled → DeployLatestRelease / DeployNewRelease / DeployLatestReleaseToEnvironment`。`types` 只返回本次发布可启用的组合 |
| 处理器 | `ValidateAsync(definition, ct)` 校验配置并返回授权范围。`EvaluateAsync(definition, observation, cursor, ct)` 返回下一游标、检查摘要和零到多个待处理项，每项含事件键、来源证据、动作输入或跳过原因。纯轮询无变化时只推进检查状态 |
| 执行动作 | `ProjectTriggerDefinition` 给出项目和 Space。已有 Release 的计划携带 `ReleaseId`、`EnvironmentId`，`AutoDeploy` 还要求非空 `SpecificMachineIds`。新 Release 的计划提供 `ChannelId`、可选 `Version`、`SelectedPackages` 和 `PackageReferenceFingerprint`。动作结果返回 `ReleaseId`，创建部署时也返回 `DeploymentId` 和 `TaskId` |

### 一次触发如何安全落地

1. **观察变化并留存输入。** `State` 按 `(TriggerId, SourceKey)` 隔离来源。一个数据库事务内以预期游标和配置修订为条件，插入本批全部 Run 并推进游标。`Run` 对 `(TriggerId, OccurrenceKey)` 建唯一索引。键来自业务事实，Git 用仓库/ref/SHA，Feed 用选定 Channel 和规范化后的依赖身份及版本组合，Scheduled 用计划 UTC 时刻，Target 用事件 ID、环境和机器 ID。一次观察要独立处理多个目的环境或租户时，把该范围计入键。编辑描述或进程重启都应识别到原来的事实。
2. **把决定保存下来。** Run 保存必要的来源证据与对应版本的配置，以及最终采用的基线 Deployment、Release、包版本或目标集合。敏感输入仅保存受控引用。临时错误使用有限次数的退避重试，次数耗尽后记录失败原因。公共 worker 用 PostgreSQL 行锁和 `FOR UPDATE SKIP LOCKED` 领取到期记录，在同一事务复核 Run 状态并提交动作。如果进程在提交前退出，数据库回滚并释放锁，下一次扫描可重新领取。停用、删除或修改执行配置会阻止尚未提交的旧动作，保留原因和历史。
3. **原子创建业务对象。** Release、所需快照、选包和 `Run.ReleaseId` 在同一个 PostgreSQL 事务内提交。创建 Deployment 前沿用现有预检，随后在同一事务写入 Deployment、真实 ServerTask、Run 关联和待派发状态。创建并部署模式依次完成两步，预检阻断时整体回滚。外部来源查询在事务外完成，执行时校验配置及版本分配。项目/Channel 的版本号唯一索引处理版本冲突，Run 的事件键识别同一次触发。[Release 创建](../src/Squid.Core/Services/Deployments/Release/ReleaseService.cs#L104) · [Deployment 创建](../src/Squid.Core/Services/Deployments/Deployments/DeploymentService.cs) · [动作事务](../src/Squid.Core/Services/ProjectTriggers/Runtime/ProjectTriggerWorker.Execution.cs) · [版本唯一索引](../src/Squid.Core/Persistence/DbUpFiles/001_initial_schema.sql#L1265)
4. **可靠投递同一个 Task。** PostgreSQL 提交后才把 Task ID 投给 Hangfire。入队失败或返回空 Job ID 时，worker 在剩余重试次数内退避并重投原 Task ID。队列已接受、数据库确认前宕机也复用这个 ID。达到上限后 Run 记为 `Failed`，保留已提交的对象和关联 ID，自动重试结束。`DeploymentPipelineRunner` 直接实现 `IDeploymentTaskExecutor`，通过同一个 `ProcessAsync` 处理投递准入、同 Task 互斥和环境并发等待：普通投递默认 `resume=false`，手工恢复及人工干预完成后的恢复显式传入 `resume=true`，延期重投保留该标记。准入后继续按原有 Phase 顺序执行，通过 Lifecycle 发事件并由 CompletionHandler 收尾。手工创建、取消和恢复沿用现有入口。Trigger 投递可认领没有租约持有者的 `Cancelling` 任务并完成取消。有活动租约时由持有者处理，过期租约由回收服务处理。宿主关停时，执行器沿用普通部署的暂停语义，将任务记为 `Paused`，保留 checkpoint 和远端脚本，并允许 Hangfire 重投后继续。人工干预等普通暂停仍要求显式恢复。[待派发任务](../src/Squid.Core/Services/ProjectTriggers/Runtime/ProjectTriggerWorker.Dispatch.cs) · [任务入口](../src/Squid.Core/Services/DeploymentExecution/IDeploymentTaskExecutor.cs) · [重复投递与手工恢复用例](../tests/Squid.IntegrationTests/Deployments/Pipeline/IntegrationTriggerTaskExecution.cs)
5. **给出操作者能看懂的结果。** 运行历史区分待处理、等待派发、已创建 Release、已派发 Deployment、跳过、失败，保留尝试次数、时间和原因。例子包括“源环境没有成功部署”“机器已是该版本”“运行时授权已撤销”。派发后的执行中、成功或失败跟随关联的 ServerTask。在业务对象已提交后停用 Trigger，已创建的任务由现有任务页管理。

### 两个需要共同维护的业务入口

**创建 Release。** 公共动作将项目、Channel、分配或明确指定的版本号以及固定的 `SelectedPackages` 传给 `CreateReleaseCommand`。默认自动版本策略按 Channel 串行分配。创建时对当前流程和变量取快照，内容相同的快照可以复用。Git 分支负责把监视仓库、ref 和 SHA 固定到 Release 的对应步骤 Git 引用。项目流程快照仍取自项目配置来源，两个仓库可以不同。[创建命令](../src/Squid.Message/Commands/Deployments/Release/CreateReleaseCommand.cs#L9) · [版本分配](../src/Squid.Core/Services/ProjectTriggers/Actions/TriggerReleaseVersion.cs) · [选包与复核](../src/Squid.Core/Services/ProjectTriggers/Actions/TriggerReleasePackageResolver.cs)

**选择已有部署并创建 Deployment。** 公共查询按 Space、项目、环境及可选 Channel 取成功记录，返回 `DeploymentId`、`ReleaseId`、实际 Channel、完成时间、请求和快照 ID。它同时核对完成记录及关联任务终态，按完成时间及 ID 排序。环境基线会排除 Target 补部署、指定或排除部分机器的部署，以及请求或机器选择器格式损坏的记录。未保存机器选择器的旧部署仍可用于选择环境的 Release，具体机器的成功覆盖需要单独的执行记录。单独跳过某个步骤仍可形成环境基线。Target 的基线与机器覆盖判断、Scheduled 的来源和目的环境选择分别放在各自分支。[公共成功部署查询](../src/Squid.Core/Services/ProjectTriggers/Actions/TriggerDeploymentHistory.cs)

### 在公共分支上接入一个来源

先用 `GET /api/project-triggers/types?projectId=17` 读取当前用户可访问项目的能力表。生产构建在首次接入具体来源前返回空的 `data` 数组。测试宿主注册 `Scheduled` 与 `DeployNewRelease` 的组合，示范如何沿真实管理、授权和部署路径接入。下面的 Channel 和环境字段属于这个测试处理器。[消息契约](../src/Squid.Message/Models/ProjectTriggers/ProjectTriggerDto.cs) · [测试来源](../tests/Squid.E2ETests/Infrastructure/ProjectTriggerE2EFixture.cs)

在注册了示例处理器的 API 宿主上，`POST /api/project-triggers/create` 可使用以下请求体。仓库端到端用例通过 Mediator 提交同一命令。

```json
{"projectId":17,"name":"Test 部署","triggerType":"Scheduled","actionType":"DeployNewRelease","configuration":{"schemaVersion":1,"channelId":4,"environmentId":9}}
```

例如，创建成功时 `data` 为 `{"id":31,"revision":1,"executionUserId":8,"isDisabled":false,"isDeleted":false}`。更新是完整替换，需传 `projectId`、`triggerId`、`expectedRevision` 及新的 `name`、`triggerType`、`actionType`、`configuration`。要保留描述也须传 `description`，省略会清空它。启用、停用或删除只需前三个字段。修订号冲突时，`msg` 提示刷新后重试，调用方应先重新读取详情。`GET /api/project-triggers/history?projectId=17&triggerId=31` 返回 `data.count` 和 `data.items`。`ReleaseCreated` 可直接关联 `releaseId`，`Dispatched` 通过 `deploymentId` 和 `taskId` 追踪实际部署结果。[写入命令](../src/Squid.Message/Commands/ProjectTriggers/CreateProjectTriggerCommand.cs) · [查询](../src/Squid.Message/Requests/ProjectTriggers/GetProjectTriggerHistoryRequest.cs)

1. 实现 `IProjectTriggerHandler`，声明一组已支持的类型与动作。`ValidateAsync` 解析带版本的配置并返回涉及的 Channel、来源和目的环境。`EvaluateAsync` 返回下一游标、检查摘要和零到多条包含稳定 `OccurrenceKey` 的事实。一个事实返回动作计划或可公开的跳过原因，配置与证据只存凭据引用。[处理器契约](../src/Squid.Core/Services/ProjectTriggers/ProjectTriggerContracts.cs)
2. 事件发布者调用 `IProjectTriggerObserver.ObserveAsync`。需要轮询的分支另实现 `IProjectTriggerPollingSource`，为每个来源给出稳定的 `SourceKey`，在评估结果中给出下次 UTC 到期时间。扫描器每分钟唤醒，精确的日期、时区及停用后恢复规则由 Scheduled 分支实现。[观察入口](../src/Squid.Core/Services/ProjectTriggers/Runtime/ProjectTriggerObserver.cs) · [轮询入口](../src/Squid.Core/Services/ProjectTriggers/Runtime/ProjectTriggerDueScanner.cs)
3. 新建 Release 的来源在评估阶段调用 `ITriggerReleasePackageResolver.PrepareAsync`，冻结选包和流程依赖指纹。执行阶段复核选包与实际流程快照。带包的流程在查询 Feed 和执行前均校验执行人的 `FeedView` 权限。默认版本从 `1.0.0` 开始，已有版本按数字比较 `major.minor.patch` 并递增补丁号。若 Channel 历史含两段版本或预发布后缀，或项目声明了自定义版本策略，默认自动分配会失败。选已有 Release 的来源可用 `ITriggerDeploymentHistory.FindLatestSuccessfulAsync` 查询同项目、环境、可选 Channel 最近一次成功的完整部署。[选包接口](../src/Squid.Core/Services/ProjectTriggers/Actions/TriggerReleasePackageResolver.cs) · [成功部署查询](../src/Squid.Core/Services/ProjectTriggers/Actions/TriggerDeploymentHistory.cs) · [端到端接入示例](../tests/Squid.E2ETests/ProjectTriggers/ProjectTriggerE2ETests.cs)

## 你们负责的两条功能分支

### Deployment target Auto-deploy

- **先支持“机器变为可部署”。** 定义 `MachineBecameAvailable`，保存事件 ID、机器、Space、事件发生时的环境/roles 与前后状态。健康检查、启用和各注册路径在同一事务比较并写入状态变化。建议首版将已启用且健康作为可部署条件，已健康机器重新启用也应产生事件，注册为 `Unknown` 后在首次转健康时触发。相同探测结果、重复注册和仅改名称都不再触发。Octopus 的可用事件组还包括 `HasWarnings`，Squid 常规探测当前只写 `Healthy/Unavailable`，警告和其他独立机器事件可逐项补充。普通 Machine 审计在第二次保存时尽力写入，缺少状态差分。[健康状态写入](../src/Squid.Core/Services/Machines/MachineHealthCheckService.cs#L187) · [机器启用与角色更新](../src/Squid.Core/Services/Machines/MachineService.cs#L143) · [现有审计](../src/Squid.Core/Persistence/SquidDbContext.Audit.cs#L14)
- **先找基线，再判断这台机器。** 依项目和环境查当前成功部署，沿用其 Release、流程快照及原先手动跳过的动作。保留适用的人工干预与 guided failure 选项。新 Release 部署失败时，沿用上一个成功版本。若选中版本自身的最近一次环境部署失败或取消，建议按 Octopus 故障排查规则阻断，直到再次成功。同范围部署正在进行时等待结束，再读取最新基线。无成功基线、缺必填输入或历史快照无法重放时，在历史里说明原因。[无人值守部署规则](https://octopus.com/docs/projects/project-triggers/deployment-target-triggers#unattended-release-behavior) · [当前部署和在途部署](https://octopus.com/docs/deployments/patterns/elastic-and-transient-environments/keeping-deployment-targets-up-to-date)
- **补齐每台机器的部署结果。** 普通流水线现有 completion 按 Deployment 记成功，`DeployedToMachineIds` 在原生部署中缺少可靠写入。历史导入会写环境级 completion，保存的机器 ID 则属于 Octopus。Target 分支需要让普通部署和补部署都记录实际执行过适用动作的本机 ID、Release 和成功结果，并检查该机器是否已有待执行任务。默认跳过已是该 `ReleaseId` 或已有相同在途部署的机器。按项目、环境、机器范围串行检查在途工作并创建任务，两步在同一事务内完成，防止两个 Trigger 或相邻的不同事件同时给同一机器建单。运行中即使允许重部署也合并相同工作。旧历史覆盖情况记为未知，建议既有机器跳过并说明如何用一次正常部署建立基线，能确认在基线之后新建且从未部署的本机可直接补部署。[Deployment 实体](../src/Squid.Core/Persistence/Entities/Deployments/Deployment.cs#L30) · [完成回调](../src/Squid.Core/Services/DeploymentExecution/Pipeline/DeploymentCompletionHandler.cs#L18) · [历史导入](../src/Squid.Core/Services/OctopusImport/OctopusImportDeploymentHistoryImporter.Task.cs#L283)
- **在创建和执行两端限定目标。** 传非空、合法的 `SpecificMachineIds`，运行前复核机器、Space、环境、角色与可用性。用持久化的 Run 与 Deployment 关联识别补部署，名单缺失或损坏就终止该次补部署。普通解析器把空名单当作全环境。当前 Planner 对任何可运行步骤找不到匹配角色都会阻断，因此要给补部署明确步骤范围。例如只补 `web-02` 时跳过仅匹配 `db` 的目标步骤，保留基线手动跳过项，至少一项目标动作适用才建任务。服务端和 StepLevel 步骤按基线可执行语义处理，人工审批照常生效。记录 `BaselineDeploymentId` 和补部署来源，环境基线及后续阶段推进排除这次局部补部署。现有 `AutoDeployService` 属于部署完成后的 Lifecycle 推进，其入队仍用 `ProcessAsync(0)`。[目标筛选](../src/Squid.Core/Services/DeploymentExecution/Filtering/DeploymentTargetFinder.cs#L67) · [按角色规划步骤](../src/Squid.Core/Services/DeploymentExecution/Planning/DeploymentPlanner.cs#L122) · [Lifecycle 推进](../src/Squid.Core/Services/Deployments/LifeCycle/AutoDeployService.cs#L40)

### Scheduled deployment

- **先交付来源环境晋级或重部署。** `DeployLatestRelease` 配置来源、目的、可选 Channel 和 `ShouldRedeploy`。未选 Channel 时，从该项目来源环境所有 Channel 的成功部署中选择，保留选中 Release 的 Channel。目的环境已部署同一个 `ReleaseId` 且关闭重部署时记为跳过。不同 Channel 可以使用相同版本文本，比较时以 Release 的身份为准。来源与目的可相同，打开重部署时仍能定期重跑。该开关判断目的环境的版本，Target 的同名开关判断具体机器。[动作与 Channel](https://octopus.com/docs/projects/project-triggers/scheduled-deployment-trigger) · [定时重部署开关](https://github.com/OctopusDeploy/Issues/issues/6036)
- **再接另外两种模式。** `DeployLatestReleaseToEnvironment` 取选定 Channel 最新已有 Release，建议 Squid 按创建时间、ID 明确“最新”，验证目的环境属于初始可达范围。`DeployNewRelease` 调用公共 Release 解析和创建，再对同一目的环境建部署。两者均验证选定 Channel 的 Lifecycle。起始范围应包括开头连续的可选阶段和首个必经阶段，Octopus 2026.1 对这两个动作都修复过只显示第一阶段的问题。复用 Squid 的 Release 级别预检时也要覆盖相反情况。当前求值器遍历全部阶段，后置可选阶段会抬高已完成阶段下标。首个必经阶段尚未成功时，应阻止部署到其后的环境。[阶段求值](../src/Squid.Core/Services/Deployments/LifeCycle/LifecycleProgressionEvaluator.cs#L94) · [Release 级预检](../src/Squid.Core/Services/Deployments/Deployments/DeploymentService.Preview.cs#L289) · [Latest 修复](https://github.com/OctopusDeploy/Issues/issues/9810) · [New 修复](https://github.com/OctopusDeploy/Issues/issues/9814)
- **先交付可解释的时钟规则。** 建议首版支持每周指定日/每日的本地时刻及 IANA 时区，以 UTC 保存 `NextDueAt` 和本次计划时刻。到点再选择 Release 并立即创建部署。计划时刻写入 Run，部署命令留空 `QueueTime`，避免迟到的扫描被预约时间窗口校验拦下。春季跳时跳过不存在的本地时刻，秋季重复时刻取较早一次。服务停机跨过多个时刻时只补最近一次并记录合并数量，主动停用后重新启用则从下一个未来时刻开始。时钟可注入，以同一个计算器驱动预览和扫描。后续再加日内间隔、月计划和自定义 Cron。Octopus 自定义 Cron 有秒位、六个必填字段及可选年份，与现有健康检查的 Cronos 默认解析及分钟扫描粒度有差异。[Octopus Cron](https://octopus.com/docs/projects/project-triggers/scheduled-deployment-trigger#cron-expression) · [现有 Cron 入口](../src/Squid.Core/Services/Machines/MachineHealthCheckService.cs#L278) · [预约窗口校验](../src/Squid.Core/Services/Deployments/Validation/Rules/QueueWindowDeploymentValidationRule.cs#L5)

## 并行开发和合入顺序

| 顺序与建议分支 | 责任方 | 可独立验收的交付 |
| --- | --- | --- |
| 公共分支 `Trigger-Feature` | 公共负责人，四方评审 | 三张表及迁移、五种动作、管理与历史 API、处理器契约和测试来源。运行时统一提供执行身份、去重、领单、Release 和 Deployment 创建、成功部署查询及任务投递恢复 |
| `trigger-scheduled` | 你们的 Scheduled 负责人 | 时区和到期扫描、三种动作的配置与校验。先交付来源环境晋级，再接无来源的已有版本和新建模式 |
| `trigger-target-auto-deploy` | 你们的 Target 负责人 | 机器状态变化、目标覆盖记录、基线选择、局部步骤规划、指定机器执行 |
| `trigger-git` | Git 同事 | 读取流程引用的仓库和凭据，按 ref、差异路径与 glob 筛选，固定 SHA 后调用公共 `CreateRelease`。建议首次启用时以当前 ref 建基线，只处理其后的变化 |
| `trigger-external-feed` | Feed 同事 | 从流程选监视依赖，按 Channel 查询已用版本，处理 Feed 版本排序和已选包集合，调用公共 `CreateRelease`。建议无历史 Release 时先建立观测基线，之后遇到新版本再创建。现有 `LastReleaseVersion` 按整个项目取最近 Release，需增加 Channel 条件。[现有版本基线](../src/Squid.Core/Services/Deployments/Process/DeploymentPackageReferenceService.cs#L203) |

建议四个功能分支都从 `Trigger-Feature` 验证通过的同一提交分出。`Squid.Message` 放公共命令、查询与 DTO，`Squid.Core` 放持久化、服务和 worker，`Squid.Api` 放 Controller。每个功能分支在自己的类型目录加入处理器、配置和测试，自有数据表单独发 DbUp 迁移。公共契约修改回 `Trigger-Feature` 评审，再同步给各支。先合入一个 Scheduled 来源环境用例验证整条部署链，再按各分支验收结果开放类型。Git、Feed 和 Scheduled 新建模式均可复用公共 Release 选包及版本分配。若还要承诺“创建 Release 后自动推进环境”，另行补齐 Lifecycle 的真实建单与端到端测试。

本方案建议首个可用版本优先覆盖非租户项目。当前创建 Deployment 的命令没有租户字段。保留按租户扩展执行范围的契约，启用时明确拒绝尚未支持的租户配置。显式指定机器补部署版本的 override、短时间内多机合批、完整 Cron 编辑器、Octopus Trigger 导入均可独立排期。项目的 `AutoCreateRelease` 只是一个布尔字段，导入兼容性表也将 Trigger 标为暂未支持，已有项目可从零条触发器开始。[创建部署命令](../src/Squid.Message/Commands/Deployments/Deployment/CreateDeploymentCommand.cs#L9) · [项目字段](../src/Squid.Core/Persistence/Entities/Deployments/Project.cs#L24) · [导入兼容性](../src/Squid.Core/Services/OctopusImport/Octopus/OctopusResourceMetadata.cs#L36)

## 合入验收

| 层级 | 先写的验收用例 | 通过条件 |
| --- | --- | --- |
| 公共契约与 API | 注册一个测试类型。验证配置往返、错误动作、过期修订、删除后读历史、同 Space 不同项目、跨 Space、执行人被撤权 | 未注册类型无法启用，越权请求无业务副作用，记录可追到操作者和具体原因 |
| PostgreSQL 与队列集成 | 双 worker 处理同一事件，分别在写游标前、业务事务中、提交后入队前、已入队未确认时中断，以及同一 Task 被投递两次 | 游标与 Run 一致，恢复后每个执行单元最多创建一组应有对象。重试窗口内恢复时投递原 Task。用尽重试次数后保留 Task ID 和最终原因。重复投递只启动一次脚本，手动恢复暂停任务仍可用 |
| Scheduled 端到端 | Test 已成功 `1.8.0`，较晚创建的 `1.9.0` 在 Test 失败。覆盖不同 Channel 同版本号、来源与目的相同、无成功历史、首段可选与中段可选、DST 和停机补跑 | 来源晋级选 `1.8.0`，无来源模式按 Channel 选择，新建模式只创建一个 Release 和一项部署。原计划时刻唯一，跳过有原因 |
| Target 端到端 | 同环境有 `web-01`、新加入的 `web-02` 和 `db-01`。重复上报健康、事件重放、两个 Trigger 的不同事件同时命中、在途任务、手动跳过、混合角色、运行前移除目标 | 只为 `web-02` 创建一项部署，仅该机器收到适用的目标动作，原跳过项继续跳过。局部部署不推进其他环境，目标无效时零派发。新版本失败、同版本失败及旧数据覆盖未知可分别解释 |
| Git / Feed 契约 | 同一 SHA 两次、仅改排除路径、相同包版本反复出现、违规 Channel 版本、两个新版本并发创建 Release | 只对符合配置的新输入建 Release，记录可追到 ref/SHA 或具体版本，依赖集合固定，重试复用原结果 |

按仓库约定，纯匹配和日期运算先写单测，游标、唯一约束和派发恢复用真实 PostgreSQL 集成测试，目标范围及定时三种动作用现有部署流水线跑端到端。公共分支的端到端用例注册一个测试来源，通过管理和授权入口实际创建 Release、Deployment 和 ServerTask，捕获队列回调与最终脚本请求。四个功能分支分别验收上表的来源规则。[仓库测试约定](../CLAUDE.md)

在仓库根目录可运行以下公共分支用例。集成与端到端测试使用测试配置指定的 PostgreSQL 实例，账号需有创建隔离测试库的权限。

```sh
dotnet test tests/Squid.UnitTests --filter 'FullyQualifiedName~ProjectTriggers|FullyQualifiedName~AuditDocumentRegistryTests'
dotnet test tests/Squid.IntegrationTests --filter 'FullyQualifiedName~ProjectTriggers|FullyQualifiedName~IntegrationTrigger'
dotnet test tests/Squid.E2ETests --filter 'FullyQualifiedName~ProjectTriggerE2ETests'
```
