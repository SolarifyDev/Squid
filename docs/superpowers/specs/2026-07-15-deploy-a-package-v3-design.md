# Deploy a Package V3 设计

## 1. 目标

在 V1 安装闭环与 V2 完整 step 控制面之上，将 `Deploy a Package` 收尾为完整可用能力：

1. 配置改写能力默认常显且真实执行。
2. 安装策略（purge/preserve、skip、retention、current/rollback）真实可用。
3. 包源不再限制为 NuGet only；可下载 archive 源端到端工作。
4. 不破坏 V1 安装闭环与 V2 通用控制字段语义。
5. 不做 Configure features 开关体系；能力直接暴露在步骤编辑器中。

V3 是 Deploy a Package 的收尾开发，不再把下列能力延后到 V4。

## 2. 背景与约束

### 2.1 继承自 V1/V2 的稳定决策

- Action type：`Squid.TentaclePackage`
- 格式：`.nupkg` / `.zip` / 其他可解压 archive（W3 扩展）
- Server 下载后上传目标机，不下发 Feed 凭据
- Release 固定版本是唯一版本事实来源（步骤编辑器不选版本）
- 目标：Tentacle Listening / Tentacle Polling / SSH POSIX/Bash
- 安装目录：Versioned 或 Custom
- Conventions：Windows `.ps1`，Linux/SSH `.sh`
- Hash：全链路 SHA-256
- V2 通用控制：Execution Location / Start Trigger / Retries / Timeout / Rolling / Conditions

### 2.2 已有可复用底座

- Calamari：`SubstituteInFilesStep`、`ConfigurationTransformsStep`、`StructuredConfigVariablesStep`
- 属性 canonical 已在 Calamari 部分落地；IIS 仍使用 legacy 前缀
- 前端 IIS editor 有同类字段语义可参考（但 Deploy Package 不走 feature 开关）
- External Feed 已有 NuGet/GitHub/Docker/Helm 的 search/version strategy；Deploy Package acquisition 仍硬拒非 NuGet

### 2.3 明确缺口

- `.NET Configuration Variables`（appSettings/connectionStrings 类）缺少通用 Calamari step
- `deploy-package` 安装路径尚未挂接配置改写 steps
- 安装策略语义未实现
- 编辑器仍 NuGet-only 过滤；acquisition 拒绝非 NuGet

## 3. 已确认范围

### 3.1 必须做（全部默认常显）

| # | 能力 | 行为 |
| --- | --- | --- |
| 1 | .NET Configuration Variables | 开关；扫描安装目录 `.config`，按变量名替换 appSettings / applicationSettings / connectionStrings |
| 2 | .NET Configuration Transforms | 开关；默认 `*.Release.config` + `*.{Environment}.config`；Additional transforms 多行 |
| 4 | Substitute in Files | 开关；Target files globs；可选 substitution 失败即失败 |
| 5 | Structured / JSON Configuration Variables | 开关；Target files；结构感知叶节点替换 |
| 6 | 非 NuGet 包源 | 编辑器放开全部 External Feed；可下载 archive 源可获取；Docker/Helm 等不可静默安装类型显式报错 |
| 7 | purge / preserve | 是否清理包中不存在的旧文件；preserve 路径排除 |
| 8 | skip-if-already-installed | 同 packageId+version 已安装则跳过本步安装与配置改写 |
| 9 | 旧版本 retention | Versioned 模式保留最近 N 个版本目录 |
| 10 | 自动回滚 / current 指针 | Versioned 维护 `current` 指针；失败回滚 |

### 3.2 明确不做

- Configure features 真体系 / feature 勾选面板
- 步骤编辑器内选择 package 版本
- 把 Docker image / Helm chart 伪装成文件系统包静默安装

### 3.3 UI 原则

- 上述能力均为默认常显分区或 Installation Options 内选项
- 不依赖任何 feature 开关才能显示
- V2 的 Configure features 按钮可保留壳或移除展示，但不是 V3 交付能力

## 4. 核心决策

### 4.1 收尾一次做完，实现分波不砍需求

采用「一份总设计 + 分波实施」：

1. W1 配置改写
2. W2 安装策略
3. W3 包源
4. W4 回归收口

分波只解决依赖与风险，不表示范围延后。

### 4.2 复用 Calamari 配置 steps，扩展 package 安装挂载

配置改写在目标侧安装目录执行。优先复用已有 steps，挂到 `deploy-package` / `PackageInstallationCoordinator`，避免在前端或 server 侧复制改写逻辑。

顺序：

```text
hash verify
→ extract to staging
→ Custom 模式下复制已有 final 到 staging（保持 V1）
→ SubstituteInFiles
→ ConfigurationTransforms
→ ConfigurationVariables
→ StructuredConfigurationVariables
→ purge/preserve 语义应用到 commit 结果
→ commit staging → final（backup）
→ current 指针切换 / retention 清理
→ PreDeploy / PostDeploy conventions
```

### 4.3 属性写 canonical，可读 legacy

Deploy Package 只写入 handler-agnostic 属性名。若运行时同变量集存在 IIS legacy 名，Calamari 可读 fallback（与现有 transforms/substitute 一致）。

### 4.4 配置改写走 variables 注入；安装策略由 package properties 驱动

- 配置改写：通过 action properties → 目标变量集驱动 Calamari steps
- 安装策略：写入 package 相关 action properties，由 `PackageInstallationCoordinator` 消费
- `DeployPackageActionHandler` 继续负责 package identity + 路径 intent；不把 retries/timeout 塞进 intent

### 4.5 Versioned vs Custom 对策略的适用

| 策略 | Versioned | Custom |
| --- | --- | --- |
| skip-if-already-installed | 支持 | 支持 |
| purge/preserve | 支持 | 支持（默认关，防误删） |
| retention | 支持 | no-op + UI 说明 |
| current + rollback | 支持 | rollback 依赖 backup；无 current 指针 |

### 4.6 非 NuGet 支持边界

**一等支持（文件系统 archive）：**

- NuGet
- GitHub release assets（zip/nupkg/tar 等可解压格式）
- Maven / 通用 HTTP 可下载 archive

**显式拒绝：**

- Docker / container registry image
- Helm chart（不能当作普通目录安装）

拒绝必须在保存校验或 acquisition 阶段给出明确错误，禁止静默失败。

## 5. 前端设计

### 5.1 编辑器分区顺序

在 V2 布局基础上扩展：

1. Header（logo / 标题；Configure features 壳可选）
2. Step Name
3. Package（Feed 不限 NuGet + Package ID；版本仍在 Release 选）
4. Target Roles
5. Installation Directory
6. .NET Configuration Variables
7. .NET Configuration Transforms
8. Substitute Variables in Files
9. Structured Configuration Variables
10. Installation Options（purge/preserve、skip、retention、current/rollback）
11. Execution Location
12. Conditions / Start Trigger / Retries / Rolling / Timeout

### 5.2 Package 区变更

- Feed 列表：全部 External Feed
- 对不支持安装的 feed type：选择后显示 error/warning，阻止保存或在保存时硬失败
- 保留防抖搜索 + stale-response guard
- Feed 变化清空 Package

### 5.3 各配置区字段

#### .NET Configuration Variables

- `enabled` checkbox：Replace entries in `.config` files
- 说明：匹配变量名到 appSettings key / connectionStrings name 等

#### .NET Configuration Transforms

- `enabled`：Run default XML transforms
- `environmentName`：默认 `#{Squid.Environment.Name}`
- `additionalTransforms`：多行 `transform => base`

#### Substitute in Files

- `enabled`
- `targetFiles`：多行 glob
- `failOnUnresolved`：substitution 失败是否 fail deploy

#### Structured Configuration Variables

- `enabled`
- `targets`：多行目标文件/glob

#### Installation Options

- `purgeBeforeInstall`
- `preservePaths`：多行路径/glob
- `skipIfAlreadyInstalled`
- `retentionCount`：整数，0 = 不清理
- `useCurrentPointer`（仅 Versioned 有意义）
- `rollbackOnFailure`

### 5.4 model

扩展 `deploy-package-model.ts`：

- normalize / build / validate 覆盖全部 V3 字段
- known property 过滤集扩展，避免重复堆叠
- 未知属性保留
- 可按职责拆分：`deploy-package-config-model.ts`、`deploy-package-install-options-model.ts`（禁止 barrel）

## 6. 后端与执行语义

### 6.1 属性约定

| 语义 | 属性 |
| --- | --- |
| Config Variables enabled | `Squid.Action.ConfigurationVariables.Enabled` |
| Transforms enabled | `Squid.Action.ConfigurationTransforms.Enabled` |
| Transforms environment | `Squid.Action.ConfigurationTransforms.EnvironmentName` |
| Additional transforms | `Squid.Action.ConfigurationTransforms.AdditionalTransforms` |
| Substitute enabled | `Squid.Action.SubstituteInFiles.Enabled` |
| Substitute targets | `Squid.Action.SubstituteInFiles.TargetFiles` |
| Substitute fail on unresolved | `Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails` |
| Structured enabled | `Squid.Action.StructuredConfigurationVariables.Enabled` |
| Structured targets | `Squid.Action.StructuredConfigurationVariables.Targets` |
| Purge | `Squid.Action.Package.PurgeBeforeInstall` |
| Preserve paths | `Squid.Action.Package.PreservePaths` |
| Skip if installed | `Squid.Action.Package.SkipIfAlreadyInstalled` |
| Retention count | `Squid.Action.Package.RetentionCount` |
| Current pointer | `Squid.Action.Package.UseCurrentPointer` |
| Rollback on failure | `Squid.Action.Package.RollbackOnFailure` |

布尔比较与现有 Calamari `IsEnabled` 对齐（`True` ignore-case）。

### 6.2 Calamari

1. 新建 `ConfigurationVariablesStep`：扫描 `*.config`，替换匹配变量的 appSettings / applicationSettings / connectionStrings。
2. 将 Substitute / Transforms / ConfigVariables / Structured 挂入 package install pipeline（在 commit 前的 staging 目录上执行）。
3. 扩展 `PackageInstallationCoordinator`：
   - skip：检测已安装同版本 → 成功跳过
   - purge/preserve：commit 时合并文件集
   - retention：Versioned 清理旧目录
   - current：维护 `current` 链接/指针目录
   - rollback：失败恢复 backup 并回退 current

### 6.3 获取层

- 修改 `PackageAcquisitionService`：移除 NuGet-only hard reject
- 按 feed type 选择下载策略（复用/扩展 `IPackageContentFetcher`）
- 不支持类型抛明确错误
- 本地落盘扩展名按实际 archive 类型，不再假定一律 `.nupkg`
- SHA-256 校验保持

### 6.4 Handler

- `DeployPackageActionHandler` 继续校验 Feed/Package/Release version/path
- 增加对不支持 feed type 的校验（若上下文可取 feed type）
- 安装策略非法值使用安全默认或拒绝保存（与现有 step 服务一致）

## 7. 错误处理

| 场景 | 行为 |
| --- | --- |
| 配置目标文件不存在 / glob 无匹配 | warning，默认不失败 |
| Transforms 单对失败 | warning + 继续；与现有 Calamari 一致 |
| Config Variables XML 解析失败 | 默认 fail；若 `IgnoreVariableReplacementErrors=true` 则 skip+warn |
| Substitute 未解析 token 且 fail 开关开启 | fail deploy |
| Skip 命中 | step success + skipped 日志；不跑 extract/配置/commit |
| Purge 命中 preserve | 不删除 |
| Retention 清理失败 | warning，不阻断本次成功 |
| Current 切换失败 | 部署失败 |
| Rollback 失败 | error + 保留 backup 路径日志 |
| 不支持 feed type | 明确错误，禁止静默 |
| 获取失败 / hash 不匹配 | 终止部署（V1 行为） |

## 8. 默认值

| 字段 | 默认 |
| --- | --- |
| 所有配置改写开关 | 关 |
| purge / skip / current / rollback | 关 |
| retentionCount | 0（不清理） |
| transforms environmentName | `#{Squid.Environment.Name}`（启用默认 transforms 时使用） |
| Custom 模式 retention/current | no-op |

旧步骤无 V3 字段时按上表默认运行。

## 9. 测试与验收

### 9.1 Unit

- model normalize/build/validate + 未知属性保留
- ConfigurationVariablesStep 替换语义
- package pipeline 中 steps 顺序与 enable 门闩
- skip / purge / preserve / retention / current / rollback
- acquisition 支持与拒绝矩阵

### 9.2 Calamari

- config rewrite 文件级测试
- install coordinator 策略测试

### 9.3 E2E

- pipeline E2E：属性进入变量/安装请求
- 真实环境 E2E：有环境则跑；阻塞时可暂停由用户处理

### 9.4 浏览器

- 全部 V3 分区可见、可保存回显
- 不支持 feed 有明确提示
- V2 控制字段不回归
- Release 仍可选包版本

### 9.5 完成标准

1. §3.1 全部能力 UI 常显且可保存回显
2. 执行语义真实生效
3. V1/V2 不回归
4. 自动化覆盖主路径
5. 不支持 feed 明确报错
6. 文档与实现一致

## 10. 实施波次

### W1 — 配置改写

- 前端：Variables / Transforms / Substitute / Structured 常显区 + model
- 后端/Calamari：ConfigurationVariablesStep + package pipeline 挂接
- 测试：model + Calamari rewrite

### W2 — 安装策略

- 前端：Installation Options
- Calamari coordinator：skip/purge/preserve/retention/current/rollback
- 测试：coordinator 单测

### W3 — 包源

- 前端：放开 feed；不支持类型校验
- acquisition：非 NuGet archive 下载；拒绝 Docker/Helm
- 测试：acquisition 矩阵 + 相关 unit

### W4 — 收口

- 全量回归（Unit / Calamari / 前端）
- 浏览器清单
- 可选 real E2E
- 更新 plan 勾选与 design 状态

## 11. 风险

| 风险 | 控制 |
| --- | --- |
| 配置改写逻辑分叉 | 共享 Calamari steps，只改挂载 |
| Custom + purge 误删 | 默认关；preserve；单测边界 |
| current/retention 与 Custom 冲突 | 仅 Versioned 生效 |
| 非 NuGet 协议碎片 | W3 先 archive；容器/chart 硬拒绝 |
| 编辑器膨胀 | 拆 model 文件；禁止 barrel |
| 破坏 V1/V2 | 回归门禁 |

## 12. 共识摘要

- V3 是 Deploy a Package 收尾，范围完整不延后。
- 不做 Configure features 开关体系；能力默认常显。
- 配置改写复用/补齐 Calamari；安装策略扩展 coordinator。
- 非 NuGet 支持可安装 archive；Docker/Helm 明确拒绝。
- 版本继续由 Release 固化。
- 实施分 W1–W4，全部属于 V3 交付。

## 13. 状态

- design：approved for planning（用户已批准设计）
- next：writing-plans 重写 `docs/superpowers/plans/2026-07-15-deploy-a-package-v3.md` 为可执行任务计划
