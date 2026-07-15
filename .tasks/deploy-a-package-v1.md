---
task: deploy-a-package-v1
status: pending
created: 2026-07-15
updated: 2026-07-15
repositories:
  - Squid
  - SquidWeb
design: docs/superpowers/specs/2026-07-15-deploy-a-package-design.md
---

# Deploy a Package V1 实施计划

## 目标

实现 `Squid.TentaclePackage` 的核心部署闭环：在 SquidWeb 配置外部 NuGet package，在创建 Release 时固定版本，由 Squid Server 下载并校验归档，然后部署到 Tentacle Listening、Tentacle Polling 或 SSH Linux 目标的持久版本化目录，并运行 package 内的 `PreDeploy` / `PostDeploy` convention scripts。

## 工作区

- 后端：`/Users/nacho/Documents/GitHub/SolarifyDev/Squid`
- 前端：`/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb`
- 设计文档：`docs/superpowers/specs/2026-07-15-deploy-a-package-design.md`
- Octopus 参考：<https://octopus.com/docs/deployments/packages>

## 范围与约束

- V1 只支持外部 NuGet Feed，以及 `.nupkg` / `.zip`。
- Package 由 Squid Server 下载，不向目标机传输 Feed 凭据。
- Release 中的固定版本是唯一版本事实来源，部署时不回退到 latest。
- 支持 Tentacle Listening、Tentacle Polling、SSH POSIX/Bash。
- 默认使用版本化持久目录，可选择自定义绝对目录。
- 不实现设计文档“暂不包含”章节列出的能力。
- 不新增前后端测试框架或重复的 package abstraction。
- 不修改或提交用户现有的 `Squid/.env`、`SquidWeb/.env`、`SquidWeb/pnpm-lock.yaml` 等无关变更。
- 不生成或手动修改 `.d.ts` 文件。

## 子任务总览

| # | 状态 | 子任务 | 依赖 | 完成条件 |
| --- | --- | --- | --- | --- |
| 01 | pending | 基础契约与 package acquisition | - | Package identity、固定版本、SHA-256、路径计划和 intent 闭环通过单元/集成测试 |
| 02 | pending | Tentacle / Calamari 执行链 | 01 | Windows/Linux Tentacle 能安全安装 package 并运行 conventions |
| 03 | pending | SSH 执行链 | 01 | SSH Linux 能复用 cache/staging 安装 package 并运行 Bash conventions |
| 04 | pending | SquidWeb 步骤编辑器 | 01 | 可创建、编辑 `Deploy a Package` 步骤，并正确进入 Release 选包流程 |
| 05 | pending | 集成验证与收尾 | 02, 03, 04 | 主路径 E2E、前端浏览器验证、全量相关测试与文档同步通过 |

## 共识与决策

- **2026-07-15 使用语义化 intent**：新增 `DeployPackageActionHandler` 产出 `DeployPackageIntent`，由 Tentacle 和 SSH 分别渲染，不在 handler 中生成 transport-specific 脚本。
- **2026-07-15 固定版本来源**：process action 只绑定 Feed 与 Package ID，准确版本由 Release 固化；执行时缺少匹配版本必须失败。
- **2026-07-15 Server 下载**：Server 获取归档后上传目标机，不下发 Feed 凭据。
- **2026-07-15 SHA-256 统一**：`PackageAcquisitionResult.Hash`、staging requirement/plan、SSH cache/upload 和目标校验同时切换为 SHA-256，禁止 MD5/SHA-256 混用。
- **2026-07-15 目录提交**：默认版本化目录采用 staging + backup + 同文件系统 rename；自定义目录先复制旧内容到 staging，再覆盖 package 内容并提交，从而保留旧文件并支持失败恢复。
- **2026-07-15 SSH 路径解析**：`$HOME` 只能在 SSH 目标端解析。Intent 携带目录模式、安全路径片段和已展开的自定义路径，不要求 Server 预先生成 SSH 默认绝对路径。
- **2026-07-15 conventions**：Windows Tentacle 执行 `.ps1`，Linux Tentacle 与 SSH 执行 `.sh`；脚本工作目录是最终安装目录，并可读取普通变量和敏感变量。

## 01 - 基础契约与 package acquisition

### 目标

让 process package reference、Release 选包、Server acquisition、`DeployPackageIntent` 和 capability validation 使用同一组明确且可验证的 package identity、版本与摘要契约。

### 必读代码

- `src/Squid.Message/Constants/SpecialVariables.cs`
- `src/Squid.Core/Services/Deployments/Process/DeploymentPackageReferenceService.cs`
- `src/Squid.Core/Services/Deployments/Release/ReleaseService.cs`
- `src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionService.cs`
- `src/Squid.Core/Services/DeploymentExecution/Packages/HttpPackageContentFetcher.cs`
- `src/Squid.Core/Services/DeploymentExecution/Infrastructure/PackageVersionResolver.cs`
- `src/Squid.Core/Services/DeploymentExecution/Intents/DeployPackageIntent.cs`
- `src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs`
- `src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Prepare.cs`

### 设计方案

1. 在 `SpecialVariables.Action` 中补齐安装目录模式、自定义目录和安装结果变量常量；继续使用 `SpecialVariables.ActionTypes.TentaclePackage`，不新增重复 action type。
2. 修正 `DeploymentPackageReferenceService.DetectActionLevelPackageReferences`：`PackageReferenceName` 必须为 action 的 Package ID，不再为空字符串。
3. 创建 Release 时继续持久化 `ActionName + PackageReferenceName + FeedId + Version`；增加服务层校验，拒绝 package reference 与 process action 不一致或版本为空。
4. `PackageAcquisitionService` 校验 Package ID、版本和 Feed 类型；保存 NuGet 返回的原始 archive bytes，不依赖 `HttpPackageContentFetcher` 的 YAML-only `Files` 投影。
5. 将 acquisition 与 SSH staging 链的 hash 统一改为 SHA-256；保留现有 `Hash` 字段名，避免无必要 DTO 扩张。
6. 修改 acquisition phase：任何选中 package 获取失败都终止 deployment，不再记录失败后继续执行 action。
7. 新增 `DeployPackageActionHandler`：
   - 校验 Feed ID、Package ID、目录模式；
   - 从 `ActionExecutionContext.SelectedPackages` 按 action name + Package ID 找到唯一固定版本；
   - 构造 `DeployPackageIntent`，声明 package staging capability；
   - 不读取 latest，也不使用 action property 版本兜底。
8. 扩展 `DeployPackageIntent`：保存目录模式、安全路径片段和自定义路径。默认目录的绝对根由 transport 在目标端解析。
9. 新增纯函数路径组件：负责安全化 Environment、Project、Package、Version 单段目录名，校验自定义 Windows/POSIX 绝对路径，拒绝根目录、`..`、控制字符和未解析变量。
10. 在现有依赖注入位置注册 handler；更新 `ActionTypes.All`、Tentacle/SSH `SupportedActionTypes` 与 capability tests。
11. 扩展 `IntentVariableExpander`，只展开 `DeployPackageIntent` 中允许变量替换的自定义目录字段，不修改 package identity 或 Release 固定版本。

### 主要文件

- 修改：`src/Squid.Message/Constants/SpecialVariables.cs`
- 修改：`src/Squid.Core/Services/Deployments/Process/DeploymentPackageReferenceService.cs`
- 修改：`src/Squid.Core/Services/Deployments/Release/ReleaseService.cs`
- 修改：`src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionService.cs`
- 修改：`src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionResult.cs`
- 修改：`src/Squid.Core/Services/DeploymentExecution/Packages/Staging/PackageRequirement.cs`
- 修改：`src/Squid.Core/Services/DeploymentExecution/Packages/Staging/PackageStagingPlan.cs`
- 修改：`src/Squid.Core/Services/DeploymentExecution/Intents/DeployPackageIntent.cs`
- 新增：`src/Squid.Core/Services/DeploymentExecution/Handlers/DeployPackageActionHandler.cs`
- 新增：`src/Squid.Core/Services/DeploymentExecution/Packages/PackageInstallationPath.cs`
- 修改：`src/Squid.Core/Services/DeploymentExecution/Variables/IntentVariableExpander.cs`
- 修改：`src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs`
- 修改：Tentacle/SSH transport capability 声明和实际 DI 注册文件，文件位置以执行时 CodeGraph 结果为准。

### 测试

- 扩展 `DeploymentPackageReferenceService` 测试，断言 action-level reference name 等于 Package ID。
- 扩展 `ReleaseService` 测试，覆盖空版本、identity mismatch 和固定版本持久化。
- 扩展 `PackageAcquisitionServiceTests`，覆盖 SHA-256、空归档、无效 ID/版本、非 NuGet Feed。
- 新增 `DeployPackageActionHandlerTests`，覆盖成功 intent、缺字段、无 Release 版本、重复匹配和变量目录。
- 新增 `PackageInstallationPathTests`，覆盖 Linux、Windows、SSH 片段与恶意路径。
- 更新 `ExecutionIntentTests`、`CapabilityValidatorTests`、action type drift tests。
- 增加 pipeline integration test，证明 acquisition 失败会中止而不是继续。

### 验证命令

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~DeployPackage|FullyQualifiedName~CapabilityValidator"
dotnet test tests/Squid.IntegrationTests/Squid.IntegrationTests.csproj --filter "FullyQualifiedName~Package"
dotnet build Squid.sln --no-restore
```

## 02 - Tentacle / Calamari 执行链

### 目标

让 Tentacle Listening/Polling 收到 Server 下载的原始 archive，在最终目录同文件系统内安全准备和提交安装目录，并运行平台对应的 conventions。

### 必读代码

- `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Rendering/TentacleListeningIntentRenderer.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Rendering/TentaclePollingIntentRenderer.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/HalibutMachineExecutionStrategy.cs`
- `src/Squid.Tentacle/ScriptExecution/LocalScriptService.cs`
- `src/Squid.Calamari/Host/CoreCommandModule.cs`
- `src/Squid.Calamari/Commands/Package/*`
- `src/Squid.Calamari/Commands/Conventions/*`
- `src/Squid.Calamari/ServiceMessages/*`

### 设计方案

1. 两个 Tentacle renderer 接受 `DeployPackageIntent`，生成明确标记为 package deployment 的 request，并传入 acquisition result、目录计划、变量和超时。
2. 不复用当前 `CalamariPayloadBuilder` 的 YAML NuGet repack 路径。`HalibutMachineExecutionStrategy` 对 package deployment 分支直接把原始 archive、variables、sensitive variables 和控制参数作为 `ScriptFile` 发送。
3. 复用 `StartScriptCommand`、machine isolation mutex、in-flight reattach、observer 和 sensitive masking；不新增平行 RPC 协议。
4. 在 Calamari 增加专用 `deploy-package` command/handler，并注册到 `CoreCommandModule`。该命令接收 archive、hash、目录模式、目录片段/自定义路径、variables 与 sensitive variables 路径。
5. 提取可测试的 package installation coordinator：
   - 解析目标平台根目录；
   - 在 final parent 下创建 staging/backup；
   - SHA-256 校验；
   - 复用 `PackageExtractorRegistry` 和 `ArchiveSafety` 解压；
   - 按版本化/自定义目录语义提交；
   - 失败时恢复 backup，清理错误不得覆盖原始异常。
6. 复用 convention resolver/bootstrap/script engine，但让其以最终目录为 working directory；Windows 只找 `.ps1`，Linux 只找 `.sh`。
7. Convention 运行顺序为提交目录后 `PreDeploy`、空主动作、`PostDeploy`。Convention 失败保留最终目录用于诊断并使 action 失败。
8. 使用现有 service message 输出三个安装结果变量；日志包含 archive hash、路径、解压统计与阶段。
9. 确保 workspace cleanup 只删除任务临时文件，不会删除持久安装目录。

### 主要文件

- 修改：两个 Tentacle intent renderer。
- 修改：`HalibutMachineExecutionStrategy.cs`
- 修改：`ScriptExecutionRequest.cs` 或现有 execution semantics，仅增加能区分 package deployment 所必需的最小字段/枚举值。
- 新增：`src/Squid.Calamari/Host/DeployPackageCliCommandHandler.cs`
- 修改：`src/Squid.Calamari/Host/CoreCommandModule.cs`
- 新增：`src/Squid.Calamari/Commands/Package/DeployPackageCommand.cs`
- 新增：`src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs`
- 复用并按需小范围调整：`PackageExtractorRegistry`、`ConventionScriptResolver`、`ConventionBootstrap`。

### 测试

- Tentacle renderer tests：Listening/Polling request shape、平台语法、package reference。
- `HalibutMachineExecutionStrategyTests`：原始 bytes 发送、变量/敏感变量、无 YAML repack、reattach 不重复部署。
- Calamari command tests：参数校验、SHA mismatch、zip/nupkg 解压、版本化替换、自定义目录合并与恢复。
- Convention tests：Windows `.ps1`、Linux `.sh`、变量/敏感变量可见、Pre/Post 失败语义。
- Tentacle tests：workspace 路径防逃逸和持久目录不被 cleanup 删除。

### 验证命令

```bash
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~Convention"
dotnet test tests/Squid.Tentacle.Tests/Squid.Tentacle.Tests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~LocalScriptService"
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~HalibutMachineExecutionStrategy|FullyQualifiedName~Tentacle"
```

## 03 - SSH 执行链

### 目标

让 SSH Linux 目标复用现有 package cache/staging，把 archive 安装到持久目录并运行 Bash conventions，同时保持普通 Run Script package attachment 的现有行为。

### 必读代码

- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Rendering/SshIntentRenderer.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Transport/SshExecutionStrategy.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPaths.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPackageTransfer.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Packages/Staging/*`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshCachedPackageLookup.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshFileTransfer.cs`

### 设计方案

1. 扩展现有 `SshIntentRenderer`：`DeployPackageIntent` 生成 Bash direct-script request，携带一个 acquisition result 和 package deployment action properties。
2. 不改变普通 `RunScriptIntent` package attachment 的语义。`SshExecutionStrategy` 只在 action type/请求语义为 `TentaclePackage` 时进入持久安装流程。
3. staging planner 继续负责 cache hit/full upload；cache 目录只保存 archive，不能再把 `PackageExtractDir` 当最终安装目录。
4. `$HOME` 由目标端 `SshPaths.ResolveHomeDirectory` 解析；默认路径拼接安全目录片段。自定义路径使用 01 已完成的 POSIX 校验结果，目标端再次防御性校验。
5. 扩展 `SshPackageTransfer` 或新增同目录内聚 helper，生成正确引用的 Bash 命令：
   - `sha256sum` 校验；
   - 检测 `unzip`；
   - staging/backup 建立；
   - archive 解压；
   - 版本化 replace 或自定义 merge；
   - 运行 `PreDeploy.sh` / `PostDeploy.sh`；
   - 失败恢复和 finally cleanup。
6. 所有 shell 参数必须通过单一 POSIX quoting helper，不允许直接把用户路径插值进 command。
7. 通过现有 service message shell helpers输出安装目录、Package ID、Version。
8. 日志与退出码区分 hash、path、extract、commit、PreDeploy、PostDeploy 阶段。

### 主要文件

- 修改：`SshIntentRenderer.cs`
- 修改：`SshExecutionStrategy.cs`
- 修改：`SshPaths.cs`
- 修改：`SshPackageTransfer.cs`
- 修改：`SshFileTransfer.cs`、`SshCachedPackageLookup.cs`（SHA-256）
- 新增：`Targets/Ssh/Packages/SshPackageDeploymentScriptBuilder.cs`，仅负责纯脚本生成和 quoting。

### 测试

- `SshIntentRendererTests`：DeployPackage request 与普通 RunScript 不互相影响。
- `SshExecutionStrategyTests`：cache hit/full upload、默认/自定义路径、hash mismatch、cleanup、取消。
- Script builder tests：空格、单引号、特殊字符、恶意路径、唯一 action 上下文。
- SSH fixture integration：真实 OpenSSH 目标部署 zip，验证文件、conventions、输出变量和同版本 redeploy。

### 验证命令

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~Ssh"
dotnet test tests/Squid.E2ETests/Squid.E2ETests.csproj --filter "FullyQualifiedName~Ssh|FullyQualifiedName~DeployPackage"
```

## 04 - SquidWeb 步骤编辑器

### 目标

让用户在 Deployment Process 中创建和编辑 `Deploy a Package` 步骤，并让该 action 自动出现在创建 Release 的 package version selection 中。

### 必读代码

- `SquidWeb/src/pages/project-detail/DeploymentProcess.tsx`
- `SquidWeb/src/pages/project-detail/step-editors/run-script/RunScriptEditor.tsx`
- `SquidWeb/src/pages/project-detail/step-editors/iis/DeployToIisEditor.tsx`
- `SquidWeb/src/pages/project-detail/step-editors/shared/*`
- `SquidWeb/src/pages/apis/deploymentProcess.ts`
- `SquidWeb/src/pages/apis/externalFeed.ts`
- `SquidWeb/src/pages/project-detail/ReleasesCreate.tsx`

### 设计方案

1. 新增 kebab-case 目录 `step-editors/deploy-package/`，使用命名导出 `DeployPackageEditor`，实现现有 `StepEditorHandle`。
2. 将 editor 注册到 `DeploymentProcess.tsx` 的 `STEP_EDITOR_MAP['Squid.TentaclePackage']`；保留现有 step template，不创建重复模板。
3. 表单包含：步骤名称、NuGet Feed、Package 搜索、Target roles、安装目录模式、自定义目录、通用 Conditions。
4. 复用 `getExternalFeeds`、`searchExternalFeedPackages`、`TargetTagSelect`、`StepConditionsSection`、`CollapsibleSection`，不新增等价 API 或组件。
5. NuGet Feed 过滤兼容项目已有 `NuGet Feed` 与 `NuGet` 字符串；Feed 变化清空 Package。
6. Package 搜索采用防抖和 stale-response guard；未选择 Feed 时不发请求。
7. DTO builder 保留未知 action/step properties，写入既有 FeedId、PackageId、CustomInstallationDirectory 和新增 mode 属性。
8. 校验 Feed、Package、Target roles；Custom 模式校验非空并显示“V1 不删除包中不存在的旧文件”的固定说明。
9. 不在 editor 中选择版本。验证 `ReleasesCreate.tsx` 能基于后端修正后的 package reference 展示 Package ID 并保存版本。
10. UI 遵循现有 Ant Design/Tailwind 样式，避免新增 nested cards；窄视口确保字段和按钮不重叠。

### 主要文件

- 新增：`SquidWeb/src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx`
- 建议新增：`SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts`，只放纯 DTO/normalize 函数，避免 editor 文件超过 500 行。
- 修改：`SquidWeb/src/pages/project-detail/DeploymentProcess.tsx`
- 测试放在同目录或项目现有测试约定位置，不新增 barrel export。

### 测试

- 纯 model tests：新建 DTO、编辑 DTO、未知属性保留、mode 切换。
- Component tests：NuGet Feed 过滤、Feed 改变清包、必填校验、Custom 提示与保存。
- Release 页面回归：package reference name 为空的旧数据显示不崩溃，新数据使用 Package ID。

### 验证命令

```bash
pnpm test -- --run
pnpm typecheck
pnpm lint
pnpm build
```

## 05 - 集成验证与收尾

### 目标

证明 process -> Release -> acquisition -> Tentacle/SSH installation 的完整主路径可用，并完成跨仓库回归检查和稳定文档同步。

### 执行步骤

1. 增加或扩展 API/pipeline integration fixture：
   - 创建 NuGet Feed；
   - 创建 `Squid.TentaclePackage` step；
   - package reference 返回 Package ID；
   - 创建 Release 固定版本；
   - acquisition 与 action 使用同一 identity/version。
2. 构建最小测试 `.nupkg`，包含普通文件、`PreDeploy.sh/.ps1`、`PostDeploy.sh/.ps1` 和变量可见性标记。测试必须引用生产 extractor/coordinator，不在测试中重写部署逻辑。
3. Linux Tentacle E2E：验证默认目录、文件内容、Bash conventions、输出变量、同版本 redeploy 和失败保留。
4. Windows Tentacle E2E：验证 `%ProgramData%` 默认目录、PowerShell conventions、输出变量和失败恢复。
5. SSH Linux E2E：验证 `$HOME/.squid` 默认目录、cache hit、Bash conventions 和 cleanup。
6. 针对自定义目录跑一次覆盖合并与旧文件保留测试。
7. 运行相关后端全套测试；若全量 suite 过慢，至少运行所有受影响项目并记录未运行项。
8. 启动 SquidWeb dev server，使用浏览器验证：
   - 新建步骤；
   - 编辑步骤；
   - NuGet Feed/Package 选择；
   - 默认/自定义目录；
   - 桌面与窄视口；
   - 浏览器 console 无新增错误。
9. 更新设计文档中因实现产生的稳定事实；不记录调试过程或 agent 元信息。
10. 将本计划状态更新为 `review`，逐项对照完成标准；验证通过后改为 `completed`。

### 最终验证命令

```bash
dotnet build Squid.sln --no-restore
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj
dotnet test tests/Squid.Tentacle.Tests/Squid.Tentacle.Tests.csproj
dotnet test tests/Squid.IntegrationTests/Squid.IntegrationTests.csproj
dotnet test tests/Squid.E2ETests/Squid.E2ETests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~Ssh"
```

在 `SquidWeb` 执行：

```bash
pnpm test -- --run
pnpm typecheck
pnpm lint
pnpm build
```

平台 E2E 按项目已有 category/environment gate 执行：

```bash
dotnet test tests/Squid.LinuxTentacleE2ETests/Squid.LinuxTentacleE2ETests.csproj --filter "FullyQualifiedName~DeployPackage"
dotnet test tests/Squid.WindowsTentacleE2ETests/Squid.WindowsTentacleE2ETests.csproj --filter "FullyQualifiedName~DeployPackage"
```

## 风险与控制

| 风险 | 控制 |
| --- | --- |
| 现有 package acquisition 把 `PackageReferenceName` 当 Package ID | 01 先统一 identity，并用 process -> Release -> acquisition 集成测试锁定 |
| MD5/SHA-256 混用导致所有 cache miss 或误判 | 同一子任务内迁移全部生产者/消费者，添加固定摘要测试 |
| Tentacle 使用 YAML repack 丢失应用文件 | package deployment 使用原始 archive 专用分支，并断言 bytes 一致 |
| 持久目录被 workspace cleanup 删除 | final path 与 workspace 完全分离，并做 cleanup 回归测试 |
| 自定义目录部分提交破坏旧应用 | 同文件系统 staging/backup，失败恢复；cleanup 错误不覆盖主异常 |
| SSH shell injection | 单一 POSIX quoting helper + 恶意路径单元测试 |
| SSH 目标缺少 unzip/sha256sum | 修改 final 目录前做 preflight，输出可操作错误 |
| 老 Tentacle 不支持新命令 | capability/agent version 校验在 dispatch 前阻止，并明确提示升级 |
| 大 package 内存占用 | V1 延续现有 acquisition 数据模型；在实现中测量并记录限制，不在本任务额外引入流式协议重构 |
| 跨仓库提交混入用户改动 | 每次提交前检查两个 repo 的 `git status`，只暂存本任务文件 |

## 计划确认门

本文件当前为 `planning`。用户确认计划后：

1. 将 `status` 更新为 `pending`。
2. 开始 01 时更新为 `in-progress`，并同步子任务表状态。
3. 每个子任务完成后填写实际结果、验证命令及残余风险。
4. 02、03、04 仅在 01 完成后并行推进。

## 变更日志

- **2026-07-15**：根据已批准设计创建实施计划，拆分为基础契约、Tentacle/Calamari、SSH、SquidWeb、集成验收五个责任域。
