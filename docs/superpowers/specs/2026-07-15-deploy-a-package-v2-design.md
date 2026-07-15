# Deploy a Package V2 设计

## 1. 目标

在 V1 核心部署闭环已经落地的基础上，将 `Deploy a Package` 提升为与现有完整 step（尤其 `Run Script`）同级的可用步骤：

1. 编辑器可见分区对齐 Octopus / 现有 Squid step 的完整结构。
2. 通用步骤控制字段真实保存、回显，并接入执行语义。
3. 保持 V1 已完成的 package 下载、传输、安装与 convention 能力不被破坏。
4. Configure features 仅保留按钮壳，不在 V2 实现 features 内容。
5. 在文档结尾明确启动 V3 计划的任务，承接 .NET 配置相关能力。

V2 允许为功能完整性做合理的前后端配套改造，不以“最小 diff”为约束。

## 2. 背景与约束

### 2.1 继承自 V1 的稳定决策

- Action type：`Squid.TentaclePackage`
- Package 来源：外部 NuGet Feed only
- 格式：`.nupkg` / `.zip`
- Server 下载后上传目标机，不下发 Feed 凭据
- Release 固定版本是唯一版本事实来源
- 目标：Tentacle Listening / Tentacle Polling / SSH POSIX/Bash
- 安装目录：默认版本化目录，或自定义绝对目录
- Conventions：Windows `.ps1`，Linux/SSH `.sh`
- Hash：全链路 SHA-256

### 2.2 V1 已完成

- 后端 package identity、acquisition、DeployPackageIntent、Tentacle/SSH 安装路径、Calamari `deploy-package`
- 前端 `DeployPackageEditor` 基础字段：步骤名、Feed、Package ID、Target Roles、安装目录、通用 Conditions

### 2.3 V1 编辑器缺口（V2 要补）

当前 V1 编辑器相对 Octopus / Run Script 缺少：

- Execution Location
- Start Trigger
- Retries
- Rolling Deployment
- Time out
- Configure features 按钮壳
- Conditions 区内完整分区呈现（与现有完整 step 一致）

这些不是“纯装饰”，多数字段在现有 pipeline 中已有执行语义，V2 必须真实接入，而不是只画 UI。

## 3. 已确认范围

### 3.1 支持范围

#### 编辑器可见且真实可用

| 分区 | 行为 |
| --- | --- |
| Step Name | 保存 / 回显 / 校验非空 |
| Package | NuGet Feed + Package ID；Feed 变化清空 Package |
| Target Roles | 至少一个 target role |
| Installation Directory | `Versioned` / `Custom`；Custom 时要求非空路径 |
| Execution Location | 与现有 step 模式一致；默认部署到目标机 |
| Start Trigger | `StartAfterPrevious` / `StartWithPrevious` |
| Retries | 开关 + 次数（1-3） |
| Rolling Deployment | 开关 + Max parallel targets |
| Time out | 秒数；0 表示不超时 |
| Conditions | Environments / Run Condition / Required / Disabled 等 |
| Configure features | 仅按钮壳，不实现功能内容 |

#### 执行语义必须打通

- `StartTrigger` 影响步骤并行/串行编排
- `Retries` / `RetriesCount` 在步骤失败重试路径中生效
- `Timeout` 限制步骤执行时间
- `MaxParallelism` 控制目标并发
- `Execution Location` / `RunOnServer` 与现有 pipeline 判定一致
- 以上字段对 `Squid.TentaclePackage` 与对 `Run Script` 采用同一套属性约定

### 3.2 暂不包含

- `.NET Configuration Variables`
- `.NET Configuration Transforms`
- Configure features 真实功能（自定义脚本、structured vars、transforms、purge/preserve 等）
- 步骤编辑器内选择 package 版本
- 非 NuGet 包源
- Skip if already installed
- 旧版本 retention
- 自动回滚 / current 指针
- 自动提权

这些能力进入 V3 或后续独立迭代。

## 4. 核心决策

### 4.1 以完整 step 体验为目标，不走最小补丁

V2 允许：

- 重构 `DeployPackageEditor` 布局，对齐 Run Script 的完整分区结构
- 抽出/复用通用 step 控制字段的 model 与保存逻辑
- 为 property 解析、默认值、校验补齐前后端配套
- 为 execution location / retries / timeout / rolling 补测试与契约

不接受“只显示字段但执行忽略”的半成品，除 Configure features 明确是按钮壳。

### 4.2 复用现有通用属性，不发明第二套协议

优先使用已有属性名与语义：

| 语义 | 属性 / 字段 |
| --- | --- |
| Target roles | `Squid.Action.TargetRoles` |
| Run on server | `Squid.Action.RunOnServer` |
| Execution location | `Squid.Action.Script.ExecutionLocation` 或项目现有等价属性；若 package step 需要独立常量，必须与 pipeline 读取点统一 |
| Start trigger | step.`startTrigger`：`StartAfterPrevious` / `StartWithPrevious` |
| Retries enabled | `Squid.Step.RetriesEnabled` |
| Retries count | `Squid.Step.RetriesCount` |
| Timeout | `Squid.Step.Timeout`（秒；兼容已有 legacy minutes 读取逻辑） |
| Rolling / max parallelism | `Squid.Step.MaxParallelism` |
| Required / disabled / condition | 现有 step/action 字段与 Conditions 组件约定 |
| Package feed / id | `Squid.Action.Package.FeedId` / `Squid.Action.Package.PackageId` |
| Install mode / custom dir | `Squid.Action.Package.InstallationDirectoryMode` / `Squid.Action.Package.CustomInstallationDirectory` |

若实现中发现 package step 与 script step 的 execution location 属性耦合过紧，允许引入 package 侧清晰常量，但必须同步更新所有读取点，禁止前后端各说各话。

### 4.3 Execution Location 默认语义

Deploy a Package 默认：

- 在每个匹配 target role 的部署目标上执行
- 不是默认 Run once on a worker

UI 可提供与 Run Script 相同的三选项：

1. Run once on a worker
2. Run on a worker on behalf of each deployment target
3. Run on each deployment target

V2 必须保证：

- 选项 3 是默认
- 选项 1/2 的保存与 pipeline 判定正确
- 若当前环境尚未完整支持 worker 执行 package 部署，则在选择时给出明确禁用/提示，不允许静默失败

### 4.4 Configure features 只保留按钮

与现有多个 step 一致：

- 顶部保留 `Configure features` 按钮
- 点击后可弹出占位说明，或打开空/禁用列表
- 不实现 feature 勾选落库与运行时行为
- 不为 features 引入新的执行路径

### 4.5 版本选择仍在 Release

V2 不把 Octopus 的 “Version (optional)” 搬回步骤编辑器。  
步骤仍只绑定 Feed + Package ID；准确版本继续由创建 Release 固化。

## 5. 前端设计

### 5.1 编辑器结构

重构 `DeployPackageEditor`，建议分区顺序：

1. 页头：logo / 标题 / `Configure features` 按钮
2. Step Name
3. Package
4. Target Roles
5. Installation Directory
6. Execution Location
7. Conditions
   - Environments
   - Run Condition
   - Start Trigger
   - Required
   - Disabled（若现有 Conditions 组件支持）
   - Retries
   - Rolling Deployment
   - Time out

布局对齐 Run Script / IIS 的 `CollapsibleSection` 风格，不引入 Octopus MUI 视觉复刻。

### 5.2 Package 区

保留并增强 V1：

- Feed 列表只显示 NuGet 类型
- Package 搜索防抖 + stale-response guard
- Feed 变化清空 Package
- 不选择版本
- 显示“版本在创建 Release 时选择”的说明（可选，简短）

### 5.3 Installation Directory 区

- `Versioned` / `Custom`
- Custom 显示绝对路径输入
- 固定说明：`V1/V2 都不会删除包中不存在的旧文件。`
- 前端基础校验；后端路径规则仍是最终权威

### 5.4 通用控制区

对齐 Run Script：

- Start Trigger 两个 radio
- Retries checkbox + 1..3
- Rolling Deployment checkbox + max parallel targets
- Timeout seconds（0 = never）
- Conditions 复用 `StepConditionsSection`

### 5.5 DTO / model

扩展 `deploy-package-model.ts`：

- normalize：从既有 step/action properties 恢复所有 V2 字段
- build：写入全部 V2 字段
- validate：Feed / Package / Target Roles / Custom path / retries 范围 / timeout 非负 / rolling 值合法
- preserve：保留未知属性

允许为可读性拆分：

- `deploy-package-model.ts`
- `DeployPackageEditor.tsx`
- 如有必要：`deploy-package-controls.ts` 或共享 step-controls helper

禁止 barrel exports。

### 5.6 模板可见性

为降低“找不到步骤”问题：

- `Deploy a Package` 继续属于 `package` 分类
- 同时加入 `featured: true`，进入默认 Featured 列表

## 6. 后端与执行语义

### 6.1 属性透传与默认值

确保 process/step/action 保存与读取覆盖：

- StartTrigger
- RetriesEnabled / RetriesCount
- Timeout
- MaxParallelism
- RunOnServer / ExecutionLocation
- Required / Disabled / Environments / Channels / ConditionExpression

缺省值：

| 字段 | 默认 |
| --- | --- |
| InstallationDirectoryMode | `Versioned` |
| Execution Location | Deployment Target |
| Start Trigger | `StartAfterPrevious` |
| Retries | 关闭 |
| MaxParallelism | 0（不启用 rolling） |
| Timeout | 0（不超时） |

### 6.2 pipeline 接入

检查并补齐 `Squid.TentaclePackage` 在以下路径的行为：

- step batching / StartWithPrevious
- retry policy 读取
- timeout 解析与执行取消
- target parallel executor / max parallelism
- run-on-server vs target execution 分流

若现有 pipeline 已通用处理这些字段，V2 重点是保证 package step 正确写入同一字段。  
若有 action-type 白名单遗漏，必须补上。

### 6.3 DeployPackageActionHandler

Handler 继续只负责 package identity + 安装路径 intent。  
不把 retries/timeout/rolling 塞进 intent；这些属于 step 级控制，由 pipeline 处理。

### 6.4 兼容性

- 旧 V1 步骤无 retries/timeout/rolling/execution location 时，按默认值运行
- 未知属性继续保留
- 不迁移历史 Release 版本选择逻辑

## 7. 错误处理与校验

### 前端保存前

- 步骤名非空
- Feed 必选
- Package 必选
- Target roles 至少一个
- Custom 路径非空
- retries 1..3
- timeout >= 0
- max parallelism >= 1（启用时）

### 后端

- 保持 V1 package identity / 路径校验
- 非法 step 控制字段使用安全默认或拒绝保存（与现有 step 服务行为一致，不另起一套）
- worker execution 若不可用，必须在保存或执行阶段给出明确错误，不静默跳过

## 8. 测试与验收

### 8.1 前端

- model normalize/build/validate
- 未知属性保留
- Feed 变化清包
- 各控制字段保存回显
- Configure features 按钮存在且不破坏保存
- Featured 分类可见 Deploy a Package
- typecheck / lint / 相关 vitest

### 8.2 后端

- step property 读写契约
- StartTrigger / MaxParallelism / Timeout / Retries 对 TentaclePackage 生效的单元或集成测试
- 旧步骤缺字段时的默认行为
- 不回归 V1 package acquisition / intent / install 路径测试

### 8.3 浏览器验收

- 新建 Deploy a Package
- 编辑并回显全部 V2 字段
- Featured 与 Package 分类都能找到
- 保存后 Release 仍可正确选包
- 桌面/窄视口无重叠溢出

### 8.4 完成标准

V2 完成必须同时满足：

1. 编辑器可见分区达到本设计 3.1 列表。
2. 通用控制字段真实保存并影响执行语义。
3. Configure features 仅按钮壳，不实现内容。
4. V1 部署闭环不回归。
5. Featured 中可直接发现 Deploy a Package。
6. 文档结尾已定义并保留“启动 V3 计划”任务。

## 9. 实施顺序建议

1. 扩展 deploy-package model 与校验
2. 重构 DeployPackageEditor 完整分区 + Configure features 按钮壳
3. Featured 可见性
4. 后端/pipeline 对通用控制字段的接入查漏补缺
5. 前后端测试与浏览器验收
6. 启动 V3 计划文档任务

## 10. 风险

| 风险 | 控制 |
| --- | --- |
| 只补 UI、执行不生效 | 每个控制字段都要有执行路径测试或既有 pipeline 复用证明 |
| Execution Location 与 package 部署语义冲突 | 默认 Deployment Target；worker 路径不完整时显式禁用/报错 |
| 属性名与 Run Script 耦合过紧 | 允许抽公共 helper 或 package 专用常量，但读取点必须统一 |
| 编辑器文件膨胀 | 按职责拆 model/controls，不引入 barrel |
| 破坏 V1 安装闭环 | V1 package 相关测试继续作为回归门禁 |

## 11. 后续：V3 预告

V3 聚焦 Octopus 中已可见但 V2 明确不做的 package 高级能力：

1. `.NET Configuration Variables`
2. `.NET Configuration Transforms`
3. Configure features 真实能力与 feature 开关体系
4. 视需要扩展：文件变量替换、purge/preserve 等

## 12. 收尾任务：启动 V3 计划

V2 实施计划与验收通过后，必须执行以下任务，不得省略：

### 任务：启动 V3 计划

**目标**  
基于本设计第 11 节，创建 Deploy a Package V3 的设计与实施规划入口。

**动作**

1. 新建设计文档：  
   `docs/superpowers/specs/<date>-deploy-a-package-v3-design.md`
2. 以 brainstorming → writing-plans 流程推进 V3
3. V3 首批范围至少覆盖：
   - `.NET Configuration Variables`
   - `.NET Configuration Transforms`
   - Configure features 从按钮壳升级为真实可配置能力
4. 明确 V3 与 V2 的兼容策略：  
   V2 已保存的通用控制字段与 V1 安装闭环不得被破坏
5. 产出 V3 实施计划：  
   `docs/superpowers/plans/<date>-deploy-a-package-v3.md`

**完成条件**

- V3 设计文档已创建并可评审
- V3 实施计划路径已确定
- V2 文档中本任务标记完成

---

## 13. 共识摘要

- V2 目标是完整 step 体验，不是最小补丁。
- 看得见的通用控制分区要真实可用。
- Configure features 先留按钮。
- .NET 配置能力留给 V3。
- 文档结束必须以“启动 V3 计划”任务收口。
