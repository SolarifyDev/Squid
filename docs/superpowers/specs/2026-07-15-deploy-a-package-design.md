# Deploy a Package V1 设计

## 1. 目标

在 Squid 与 SquidWeb 中实现可用于真实部署的 `Deploy a Package` 核心闭环：

1. 在部署流程中配置外部 NuGet Feed、Package ID、目标角色与安装目录。
2. 创建 Release 时选择并固化准确的 package 版本。
3. Squid Server 下载归档并上传到目标机。
4. 在 Tentacle 或 SSH 目标上安全解压到持久安装目录。
5. 执行 package 内的 `PreDeploy`、`PostDeploy` convention scripts。
6. 在部署日志和输出变量中公开安装结果。

参考文档：<https://octopus.com/docs/deployments/packages>

V1 提供完整的主路径，不以一次性覆盖 Octopus Deploy 的全部 package deployment 功能为目标。

## 2. 已确认范围

### 2.1 支持范围

- Action type：`Squid.TentaclePackage`
- Package 来源：外部 NuGet Feed
- Package 格式：`.nupkg`、`.zip`
- 下载位置：Squid Server
- 部署目标：
  - Tentacle Listening
  - Tentacle Polling
  - SSH（POSIX/Bash 目标）
- 安装目录：
  - 默认使用版本化目录
  - 可配置自定义绝对目录
- Convention scripts：
  - `PreDeploy.sh` / `PreDeploy.ps1`
  - `PostDeploy.sh` / `PostDeploy.ps1`
- Convention scripts 可以读取普通变量和敏感变量。

### 2.2 暂不包含

- Squid 内置 package repository
- GitHub、Helm、Docker 等非 NuGet package 来源
- 目标机直接从 Feed 下载
- Purge installation directory
- Preserve files
- 配置转换、structured configuration variables、文件变量替换
- 在步骤编辑器中编写自定义部署脚本
- Skip if already installed
- 旧版本 retention policy
- 自动回滚或 `current` 软链接切换
- Kubernetes、Server Worker 目标
- 自动使用 `sudo` 或提升权限

这些能力不是 package 固定版本、下载、传输、安装与运行 lifecycle hooks 的必要条件。它们应在核心闭环稳定后作为独立能力迭代，避免在 V1 中引入包仓库、凭据下发、目录保留策略、配置改写和应用流量切换等额外责任域。

## 3. 核心决策

### 3.1 使用语义化 Intent

新增 `DeployPackageActionHandler`，由它解析 action 配置与 Release 固定版本，并输出已有的 `DeployPackageIntent`。Handler 不生成 transport-specific 脚本。

Tentacle 与 SSH renderer 分别把同一个 intent 转换为各自的执行请求：

- Tentacle 复用 Calamari 的安全解压和 convention engine。
- SSH 复用现有 package staging/cache，并通过 Bash 执行等价生命周期。

### 3.2 Release 是版本事实来源

部署流程 action 只保存 Feed 与 Package ID。准确版本必须在创建 Release 时选择并持久化到 `ReleaseSelectedPackage`。

部署执行不得回退到 latest，也不得在重试时重新解析版本。相同 Release 的首次部署、重试和恢复必须使用同一 package 版本。

### 3.3 Server 负责下载

Squid Server 使用现有 package acquisition 流程访问 NuGet Feed，再将归档上传到目标机。Feed 凭据不发送给 Tentacle 或 SSH 目标。

### 3.4 安装目录是持久目录

部署任务 workspace 只承载临时文件，不作为最终安装位置。Tentacle workspace GC 和 SSH work-directory cleanup 不得删除已安装应用。

## 4. 数据模型与属性契约

### 4.1 Action 属性

沿用已有属性：

| 属性 | 含义 | 必填 |
| --- | --- | --- |
| `Squid.Action.Package.FeedId` | 外部 NuGet Feed ID | 是 |
| `Squid.Action.Package.PackageId` | NuGet Package ID | 是 |
| `Squid.Action.Package.CustomInstallationDirectory` | 自定义安装目录 | 否 |

新增属性：

| 属性 | 含义 | 默认值 |
| --- | --- | --- |
| `Squid.Action.Package.InstallationDirectoryMode` | `Versioned` 或 `Custom` | `Versioned` |

步骤继续使用现有 Target roles、条件、环境范围、触发方式、重试、超时和并行度属性。

### 4.2 Package identity

普通 action-level package reference 必须使用实际 `PackageId` 作为 `PackageReferenceName`。当前空字符串行为必须修正，因为 acquisition pipeline 使用 `PackageReferenceName` 作为下载与 acquired-package lookup 的 package ID。

V1 中一个 `Deploy a Package` action 只引用一个主 package，因此 identity 为：

```text
ActionName + PackageReferenceName(PackageId) + FeedId + Version
```

### 4.3 Intent

`DeployPackageIntent` 必须携带：

- `Package.PackageId`
- `Package.Version`
- `Package.FeedId`
- 安装目录模式与展开后的目标路径
- 目标脚本语法
- step/action 名称
- package staging capability requirement

`ActionExecutionContext.SelectedPackages` 中找不到与当前 action 和 Package ID 匹配的固定版本时，handler 必须失败，不得使用 action property 中的版本或 latest。

### 4.4 输出变量

成功后输出：

```text
Squid.Action.Package.InstallationDirectoryPath
Squid.Action.Package.PackageId
Squid.Action.Package.PackageVersion
```

## 5. 默认安装目录

### 5.1 路径约定

Linux Tentacle：

```text
/var/lib/squid-tentacle/Applications/<Environment>/<Project>/<Package>/<Version>
```

Windows Tentacle：

```text
%ProgramData%\Squid\Tentacle\Applications\<Environment>\<Project>\<Package>\<Version>
```

SSH：

```text
$HOME/.squid/Applications/<Environment>/<Project>/<Package>/<Version>
```

路径片段来自已经解析的 Squid 变量：

- `Squid.Environment.Name`
- `Squid.Project.Name`
- Package ID
- Release 固定的 package 版本

每个片段必须转换为单一安全目录名，禁止目录分隔符、`.`、`..`、控制字符和平台非法字符。转换结果为空时必须失败，不使用静默占位符。

### 5.2 自定义目录

自定义目录允许 `#{Variable}`。变量展开后：

- 必须为目标平台的绝对路径。
- 不允许等于文件系统根目录或盘符根目录。
- 不允许未解析变量、`..` 路径段、NUL 或控制字符。
- Tentacle Windows 使用 Windows 路径规则。
- Tentacle Linux 与 SSH 使用 POSIX 路径规则。

目标服务账户必须已经拥有写权限。Squid 不自动提权或更改目标目录权限。

## 6. 前端设计

在 SquidWeb 新增独立的 `DeployPackageEditor`，并注册到 `STEP_EDITOR_MAP['Squid.TentaclePackage']`。

编辑器包含：

- 步骤名称
- NuGet Feed 选择器
- Package 搜索与选择
- Target roles
- 安装目录模式
- 自定义安装目录输入框（仅 Custom 模式显示）
- 现有通用步骤条件区域

行为要求：

- Feed 列表仅显示 NuGet 类型 Feed。
- Feed 变化时清空已选 Package。
- Package 搜索复用现有 external-feed package search API。
- 保存时校验 Feed、Package 和 Target roles。
- Custom 模式要求非空路径；前端只做基础校验，后端是最终权威。
- Custom 模式显示固定说明：包中不存在的旧文件不会在 V1 中被删除。
- 编辑器不选择 package 版本；创建 Release 页面继续负责版本选择。
- 保留编辑器不认识的既有属性，不使用 barrel exports。

## 7. 端到端数据流

1. 用户在 Deployment Process 中保存 `Squid.TentaclePackage` action。
2. `DeploymentPackageReferenceService` 将 action 暴露为 package reference，并使用 Package ID 作为 `PackageReferenceName`。
3. 创建 Release 页面列出该 package，并要求选择明确版本。
4. Release service 持久化 `ActionName`、`PackageReferenceName`、`FeedId`、`Version`。
5. 部署准备阶段注入 `Acquire Packages` 步骤。
6. Server 从 NuGet Feed 下载原始归档，拒绝空响应，计算 SHA-256 并保存 acquisition result。
7. `DeployPackageActionHandler` 验证 action 与 Release package identity，生成 `DeployPackageIntent`。
8. capability validation 确认目标 transport 支持 package staging 与所需脚本语法。
9. 对应 renderer 生成 Tentacle 或 SSH 执行请求。
10. 目标执行器校验归档摘要，部署到最终目录并运行 conventions。
11. pipeline 收集日志、退出码和输出变量。

## 8. Tentacle 执行

### 8.1 传输

Tentacle renderer 为 `DeployPackageIntent` 生成 package deployment request。Halibut 执行策略把 Server acquisition 得到的原始归档作为 `ScriptFile` 上传，不能通过现有 YAML NuGet packer 重新打包应用内容。

归档文件、variables、sensitive variables 与部署控制脚本进入隔离 workspace。敏感变量继续沿用现有加密传输与进程环境变量机制。

### 8.2 安装

Tentacle 调用 Calamari package deployment pipeline：

1. 解析并校验最终路径。
2. 在最终目录所在文件系统创建唯一 staging 目录。
3. 校验归档 SHA-256。
4. 使用 `IPackageExtractor` 安全解压。
5. 提交文件到最终安装目录。
6. 从最终目录运行匹配平台的 `PreDeploy` convention。
7. 执行空主动作。
8. 成功后运行 `PostDeploy` convention。
9. 发出结构化 outcome 与输出变量。

默认版本化目录提交语义：

- 首次部署：staging directory rename 到最终目录。
- 同版本重新部署：现有目录先移动到同文件系统 backup，staging 提交成功后删除 backup；提交失败则恢复 backup。

自定义目录提交语义：

- 在最终目录旁创建同文件系统 backup，先把最终目录的完整内容复制到 staging，再用新 package 内容覆盖 staging 中的同名文件。
- staging 准备完成后，将现有最终目录移动到 backup，再把 staging 原子重命名为最终目录。
- package 中不存在的既有文件通过“旧目录复制到 staging”得到保留。
- staging 提交失败时恢复 backup；提交成功后删除 backup。

解压失败不得修改最终目录。Convention 失败使 action 失败，但已经提交的安装目录保留用于诊断。

### 8.3 跨平台 conventions

Convention resolver 支持：

- Windows 只执行 `.ps1`。
- Linux Tentacle 只执行 `.sh`。
- package 同时包含两种脚本时按目标平台选择。

V1 必须保证 convention script 的工作目录是最终安装目录，并可以读取普通变量和敏感变量。

## 9. SSH 执行

SSH renderer 接受 `DeployPackageIntent`，并产生 Bash direct-script request 与单个 package reference。

执行器复用现有：

- `IPackageStagingPlanner`
- cache hit / full upload 策略
- SSH package cache

需要调整现有 SSH package 流程，使 cache 目录只保存归档，不把 cache extraction directory 当最终安装目录。部署过程为：

1. staging planner 确保归档存在于 SSH package cache。
2. 校验归档 SHA-256。
3. 在最终目录同文件系统创建 staging 目录。
4. 使用目标系统可用的归档工具安全解压。
5. 按默认版本化或自定义目录语义提交。
6. 从最终目录运行 `PreDeploy.sh`。
7. 执行空主动作。
8. 运行 `PostDeploy.sh`。
9. 通过现有 service message 机制设置输出变量。

V1 SSH 仅支持 POSIX/Bash。缺少必要的解压工具、HOME 目录或目录写权限时必须在修改最终目录前失败，并输出可操作错误。

## 10. Acquisition 与完整性

`PackageAcquisitionService` 保存 NuGet 返回的原始归档，不提前只提取部分文件。V1 不使用 `HttpPackageContentFetcher.ExtractArchive` 的 YAML-only 文件投影来判断普通应用 package 内容。

V1 将现有 `PackageAcquisitionResult.Hash`、`PackageRequirement.Hash`、`PackageStagingPlan.Hash` 及 SSH cache/upload 校验统一迁移到 SHA-256。字段名暂不扩张，所有生产者和消费者必须在同一变更中切换，避免混用 MD5 与 SHA-256。日志与目标端校验只使用 SHA-256。

下载失败条件：

- Feed 不存在或不是 NuGet Feed
- HTTP 非成功状态
- 响应为空
- Package ID 或版本为空
- 返回内容不是支持的 zip-compatible archive
- 保存本地归档失败

任一 package acquisition 失败都必须终止部署，不能记录失败后继续到 package action。

## 11. 错误处理与清理

错误信息必须标识失败阶段：

- package identity validation
- download
- local persistence
- transfer
- hash verification
- target path validation
- staging directory creation
- extraction
- final-directory commit
- PreDeploy
- PostDeploy

取消或失败时：

- 清理当前 action 的目标 staging 目录。
- 清理 workspace 中的临时归档和控制文件。
- 不删除最终持久安装目录。
- 默认版本化目录提交过程中若已创建 backup，提交失败必须尝试恢复。
- 清理或恢复失败不得覆盖原始异常；应作为附加错误记录。

## 12. 日志与可观察性

部署日志至少记录：

- Feed、Package ID、固定版本
- 下载字节数与 SHA-256
- 目标机与 communication style
- staging 路径与最终安装路径
- 解压文件数与总字节数
- `PreDeploy` / `PostDeploy` 开始、成功或失败
- 失败阶段与原因

不得记录 Feed 密码、敏感变量明文或敏感变量解密密码。

## 13. 测试与验收

### 13.1 后端单元与集成测试

- Handler：属性解析、Release 固定版本、缺失字段、package identity mismatch。
- Package reference：action-level package 使用 Package ID 作为 reference name。
- 路径规划：Linux、Windows、SSH 默认路径，自定义路径与恶意输入。
- Renderer：Tentacle Listening、Polling 与 SSH 接受 `DeployPackageIntent`。
- Acquisition：NuGet 下载、原始归档落盘、SHA-256、空包、HTTP 失败。
- Pipeline：process reference -> Release selection -> acquisition -> action request。
- Calamari：安全解压、默认目录替换、自定义目录合并、conventions、失败与取消。
- SSH：cache/full-upload、hash mismatch、安装提交、conventions、清理。

### 13.2 E2E

- Linux Tentacle：部署测试 package，验证最终文件、版本化路径与 conventions。
- Windows Tentacle：部署测试 package，验证最终文件、版本化路径与 PowerShell conventions。
- SSH Linux：部署测试 package，验证最终文件、版本化路径与 Bash conventions。
- 重新部署同一版本验证目录替换。
- PreDeploy/PostDeploy 失败验证 action 失败与诊断目录保留。

### 13.3 前端

- 表单 DTO 构建与既有属性保留。
- NuGet Feed 过滤。
- 必填校验。
- 安装目录模式切换。
- 创建与编辑步骤流程。
- `pnpm typecheck`、`pnpm lint`、`pnpm build`。
- 使用浏览器验证桌面与窄视口下没有内容重叠或文本溢出。

### 13.4 完成标准

V1 完成必须同时满足：

1. 用户可以在 SquidWeb 创建并重新编辑 `Deploy a Package` 步骤。
2. 创建 Release 页面显示该 package，并持久化明确版本。
3. 部署时 Server 下载的 Package ID 与 Release 选择一致且非空。
4. Linux Tentacle、Windows Tentacle、SSH Linux 均能将同一个测试 package 部署到预期最终目录。
5. Package conventions 能读取部署变量并影响部署结果。
6. 下载、传输、摘要、路径、解压和 convention 失败均产生明确日志。
7. 默认版本化目录部署失败不破坏先前成功版本。

## 14. 后续迭代顺序

核心闭环稳定后的建议顺序：

1. 文件变量替换与 structured configuration variables
2. Purge + preserve files
3. 旧版本 retention policy
4. Squid 内置 package repository
5. Skip-if-installed、current pointer 与自动回滚
