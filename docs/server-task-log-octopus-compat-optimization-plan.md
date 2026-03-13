# Squid Server Task Log 全量优化计划（对标 Octopus Deployment Details）

## 1. 目标与范围

### 1.1 目标
- 让 Squid 的 Deployment Details 在数据完整性、日志精细度、交互能力、性能与可维护性上接近 Octopus。
- 在保持现有 `api/tasks/{id}/details` 可用的前提下，逐步实现“更通用（generic）+ 更高效（efficient）”的任务日志平台。

### 1.2 非目标
- 不强制复制 Octopus 的 ID 语法（例如 `ServerTasks-xxxx`），内部仍可使用整型 ID。
- 不在第一阶段实现所有 Octopus 的 BFF 扩展页（如 K8s resource status 视图），先把通用任务日志平台打牢。

---

## 2. 现状与差异总览（Octopus vs Squid）

## 2.1 已具备能力（Squid）
- `GET /api/tasks/{taskId}/details` 返回 `Task + ActivityLogs + PhysicalLogSize + Progress`。
- 支持按序号增量拉取日志：`/api/tasks/{taskId}/logs?afterSequenceNumber&take`。
- 支持按节点增量拉取日志：`/api/tasks/{taskId}/nodes/{nodeId}/logs?...`。
- Activity 树模型已存在：`Task/Step/Action` 节点 + `LogElements`。

## 2.2 关键差异与缺口

| 类别 | Octopus | Squid 现状 | 影响 |
|---|---|---|---|
| details 参数 | `verbose/tail/ranges` 生效 | `verbose` 未使用，`ranges` 无 | 无法按场景控制日志颗粒度与回放范围 |
| PhysicalLogSize | 物理日志大小（字节） | 当前是 log 行数 count | UI 与容量指标语义偏差 |
| Task 头信息 | 字段丰富（Arguments, IsCompleted, FinishedSuccessfully, CanRerun, Links 等） | 字段较少 | 页面能力与运维动作受限 |
| Links | Self/Web/Raw/Cancel/... | 无标准 Links 对象 | 前后端耦合高、扩展成本高 |
| 任务操作 | cancel/rerun/prioritize/interruption | 尚未提供 | 运行态控制能力不足 |
| 原始日志 | `/raw` | 无 | 无法下载/审计/回放完整原文 |
| 分类精细度 | 细粒度消息类别与状态消息 | 仅 `Info/Warning/Error` | 调试与诊断效率一般 |
| 输出保真 | 保留每条日志上下文 | 当前会丢失部分 source/time 语义（统一用写入时刻） | 时序分析和多源合并准确度下降 |
| 任务状态派生 | 成功/失败、耗时、告警状态一致 | `DurationSeconds/ErrorMessage/HasWarningsOrErrors` 未完整回填 | 顶部状态摘要不可靠 |
| 详情查询性能 | 可大任务稳定渲染 | details 存在按节点循环取 tail（N+1） | 节点多时响应劣化 |
| 可扩展性 | 任务类型统一模型（Deploy/Runbook/...） | 主要围绕 Deploy 编排 | 后续任务类型扩展成本高 |

---

## 3. 总体方案（Generic + Efficient）

## 3.1 设计原则
- 通用任务日志内核：不绑定 Deployment，任何任务类型（Deploy、Script、HealthCheck、Maintenance）可复用。
- 双视图输出：
  - 事件流视图（分页/增量，适合实时滚动）
  - 树视图（ActivityLogs，适合详情分层阅读）
- 强语义字段：严格区分“计数”“字节数”“状态派生字段”。
- 面向扩展的 API 契约：统一 Links + Capabilities，前端按能力渲染。

## 3.2 架构分层

1) **TaskLog Core（领域层）**
- 统一日志事件模型：`TaskLogEntry`（Text/Category/Stream/OccurredAt/Source/NodeId/Sequence/...）。
- 统一 Activity 模型：`ActivityNode`（Task/Step/Action/...）。
- 统一状态派生器：`TaskStateProjector`（Duration、Error、Warnings、Progress、ETA）。

2) **Ingestion Adapters（采集层）**
- `LocalProcess` 适配器：stdout/stderr/debug 逐行映射。
- `Halibut` 适配器：保留 remote occurred/source/sequence 语义。
- 将 `ScriptExecutionResult` 从“字符串列表”升级为“结构化日志条目列表”。

3) **Query & Rendering（查询/渲染层）**
- `TaskDetailsAssembler`：一次查询构建树 + tail。
- `TaskLogStreamService`：按序增量输出。
- `TaskRawLogService`：提供原文下载。

4) **API Facade（接口层）**
- 保持已有 endpoint 向后兼容。
- 逐步增加 Octopus 风格能力接口与 Links。

---

## 4. 字段级兼容计划（Task/Activity/Log）

## 4.1 Task 视图增强
新增或规范以下字段（可分阶段）：
- `IsCompleted`（由 State 派生）
- `FinishedSuccessfully`（State == Success）
- `HasBeenPickedUpByProcessor`（StartTime != null）
- `Duration`（字符串）+ `DurationSeconds`（数值）
- `Arguments`（至少提供 `DeploymentId`）
- `QueueTimeExpiry`（若支持定时过期）
- `CanRerun/CanCancel/CanPrioritize`（能力位）
- `Links`（Self/Web/Raw/State/Details/Logs/Cancel...）

## 4.2 Activity 节点增强
- 在 DTO 输出 `Category`、可选 `Metadata`（machineName, actionType, transport）。
- 支持中间执行节点（可选）：例如 `Worker on behalf of <target>`，提升与 Octopus 一致性。
- 保留 `SortOrder`，并增加稳定次序策略（同 sort 时按 started/id）。

## 4.3 Log 元素增强
- 新增 `Stream`（StdOut/StdErr/Debug/System）。
- `OccurredAt` 使用源时间，不再统一落盘时刻。
- `Number`（节点内显示序号）按渲染时计算，不污染存储主键。
- `Detail` 用于长文本/结构化附加信息（JSON/stacktrace）。

---

## 5. API 目标态与兼容策略

## 5.1 保留并增强现有接口
- `GET /api/tasks/{id}`
- `GET /api/tasks/{id}/details?verbose={bool}&tail={int}&ranges={...}`
- `GET /api/tasks/{id}/logs?afterSequenceNumber={n}&take={m}`
- `GET /api/tasks/{id}/nodes/{nodeId}/logs?...`

## 5.2 新增接口（对标 Octopus 核心能力）
- `GET /api/tasks/{id}/raw`（文本流/文件下载）
- `GET /api/tasks/{id}/state`（轻量轮询）
- `POST /api/tasks/{id}/cancel`
- `POST /api/tasks/rerun/{id}`
- `POST /api/tasks/{id}/prioritize`
- `GET /api/tasks/{id}/status/messages`（摘要消息）
- `GET /api/tasks/{id}/queued-behind`
- `GET /api/tasks/{id}/blockedby`

## 5.3 响应形状兼容策略
- v1 保持当前 `SquidResponse<Data>`。
- v2 增加 `Links` 与 `Capabilities`，避免前端硬编码按钮逻辑。
- 可提供“Octopus-compatible projection”（仅字段投影，不改内部 ID 模型）。

---

## 6. 数据模型与 DBUp 计划

## 6.1 server_task_log 扩展字段（建议）
- `stream SMALLINT`（StdOut/StdErr/Debug/System）
- `source_type SMALLINT`（Machine/System/Worker/Server）
- `source_name TEXT`（机器名/组件名）
- `occurred_at` 保持来源时间
- `payload_json JSONB NULL`（可选，结构化日志）

## 6.2 索引优化
- 现有：`(server_task_id, sequence_number)`、`(server_task_id, activity_node_id, sequence_number)`。
- 新增：
  - `(server_task_id, occurred_at)`（时间范围检索）
  - `(server_task_id, category, sequence_number)`（按级别筛选）
  - 可选部分索引：`WHERE activity_node_id IS NULL`（root/unscoped tail）

## 6.3 N+1 消除（details 查询）
将“按节点循环查 tail”替换为单次 SQL：
- 先取 task 的 node 列表。
- 用窗口函数 `ROW_NUMBER() OVER (PARTITION BY activity_node_id ORDER BY sequence_number DESC)`。
- 每个节点取前 `tail`，再统一回正序。

预期效果：
- 从 `O(nodes)` 次查询下降到 `O(1~2)` 次。
- 大任务（数百节点）响应时间显著下降。

---

## 7. 执行写入链路优化

## 7.1 结构化日志管道
将 `ScriptExecutionResult` 从：
- `List<string> LogLines` + `List<string> StderrLines`

升级为：
- `List<ScriptOutputLine> Lines`（`Text/OccurredAt/Stream/Source/SequenceHint`）

这样可以：
- 保留 agent 侧时间；
- 不再依赖 `stderr string set` 判定分类；
- 支持 debug/trace 级别扩展。

## 7.2 任务状态一致性回填
在成功/失败收敛点统一写回：
- `DurationSeconds`
- `ErrorMessage`（失败时）
- `HasWarningsOrErrors`
- `BusinessProcessState/StateOrder`（可维护任务看板排序语义）

## 7.3 日志顺序稳定性
- 当前序号来自进程内计数器，建议改为“每 task 持久化自增序号分配器”或 DB sequence 分配，避免重试/恢复后的序号冲突与跳变。

---

## 8. 安全与合规

## 8.1 敏感信息治理
- 在入库前做敏感词/变量值脱敏（token、password、private key）。
- 对 `payload_json/detail` 进行字段级脱敏。
- Raw 下载支持权限校验和审计。

## 8.2 多租户与权限
- 所有 task/log 查询按 `SpaceId` 进行边界校验。
- Links/Capabilities 基于权限裁剪（无权限则不返回操作链接）。

---

## 9. 性能与容量策略

## 9.1 热数据策略
- Running task：短 TTL 缓存 details（1~3s）
- Completed task：长 TTL 缓存（30~300s）
- 增量日志接口绕过大对象组装，直接分页。

## 9.2 冷数据策略
- 大日志归档（对象存储）+ DB 保留索引元数据。
- Raw 接口优先读归档；热数据回源 DB。

## 9.3 压测目标（建议）
- 单任务 100k 行日志 details 响应 P95 < 1.5s（tail=50）。
- 增量日志接口 P95 < 300ms（take=500）。
- 并发 200 个运行中任务轮询不出现明显退化。

---

## 10. 分阶段实施路线（全部落地）

## Phase 0：契约冻结与回归基线（1 周）
- 冻结 v1/v2 字段契约。
- 增加集成测试基线：`details/logs/node-logs`。
- 产出兼容矩阵（字段级）。

验收：
- 输出兼容性文档与测试清单。

## Phase 1：语义修正（1~2 周）
- `verbose` 生效。
- `PhysicalLogSize` 改为字节语义（并保留 `LogLineCount`）。
- 完整回填 `DurationSeconds/ErrorMessage/HasWarningsOrErrors`。
- `Progress.EstimatedTimeRemaining` 给出可用估算。

验收：
- 详情页头部与状态摘要准确性 > 99%。

## Phase 2：查询性能与数据层增强（1~2 周）
- details 查询去 N+1。
- 新增索引与 explain 优化。
- 引入 completed task 缓存策略。

验收：
- 大任务详情响应 P95 达标。

## Phase 3：API 能力补齐（2 周）
- 新增 `/raw`、`/state`、`/cancel`、`/prioritize`、`/rerun`。
- 返回 `Links + Capabilities`。

验收：
- 前端无需硬编码 URL；操作按能力位控制。

## Phase 4：结构化日志与采集统一（2~3 周）
- 升级 `ScriptExecutionResult` 为结构化行。
- 保留 `OccurredAt/Stream/Source`。
- debug/source 在 UI 可过滤展示。

验收：
- 同一任务内多源日志时序一致，错误定位时间降低。

## Phase 5：通用任务平台化（2 周）
- 抽象任务日志内核，支持 Deploy 之外的 task type。
- 输出统一 `TaskDetails` 投影。

验收：
- 至少 1 种非 Deploy 任务复用相同详情接口。

## Phase 6：运维与治理（持续）
- 脱敏、审计、归档、容量告警。
- 周期性压测与容量评估。

---

## 11. 测试与验收矩阵

## 11.1 功能测试
- Task 头字段完整性测试（状态、耗时、错误、能力位）。
- Activity 树正确性（父子关系、排序、节点状态）。
- tail/ranges/verbose 行为测试。
- logs 增量分页一致性测试。

## 11.2 一致性测试
- `details` 与 `logs` 的交叉校验（同 sequence 不重复不丢失）。
- Raw 与结构化日志行数/顺序一致性。

## 11.3 性能测试
- 10k/100k/1M 行日志任务详情与增量拉取压测。
- 多任务并发轮询与数据库负载测试。

## 11.4 兼容性测试
- 旧前端（只识别 v1）继续可用。
- 新前端使用 `Links/Capabilities` 驱动功能。

---

## 12. 风险与规避

- 风险：历史数据字段缺失导致新字段为空。  
  规避：提供 backfill 脚本，空值降级显示。

- 风险：结构化日志改造影响现有执行链路。  
  规避：双写阶段（旧格式+新格式）并灰度切换。

- 风险：raw 下载带来敏感信息泄漏。  
  规避：权限校验 + 脱敏 + 审计日志。

- 风险：新增接口过快导致前后端联调成本高。  
  规避：先出 Links/Capabilities，让 UI 渐进接入。

---

## 13. 预期效果（对比 Octopus）

## 13.1 用户侧
- Deployment detail 可读性接近 Octopus：树状步骤 + 节点日志 + 可下载原文 + 可操作任务。
- 故障定位更快：保留 stream/source/occurredAt，减少“看起来全是 info”的噪声。

## 13.2 平台侧
- 查询性能更稳：消除 N+1，支持大日志任务。
- 任务体系更通用：不仅服务 Deployment，可复用到任意后台任务。
- 接口更演进友好：Links/Capabilities 让前端解耦。

## 13.3 维护侧
- 语义统一（bytes vs count、state vs derived fields）。
- 监控指标可观测（吞吐、延迟、失败率、容量）。

---

## 14. 首批落地清单（建议立即开工）

1. 修复 `verbose` 未生效。  
2. 将 `PhysicalLogSize` 改为字节并新增 `LogLineCount`。  
3. 回填 `DurationSeconds/ErrorMessage/HasWarningsOrErrors`。  
4. details 查询去 N+1。  
5. 新增 `GET /api/tasks/{id}/raw`。  
6. 在 `Task` 返回 `Links + Capabilities`（最小子集：Self/Web/Raw/Cancel）。

完成以上 6 项后，Squid 的 Deployment Details 将从“可用”提升到“对标 Octopus 的核心体验可用”。
