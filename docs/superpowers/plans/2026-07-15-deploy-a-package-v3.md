# Deploy a Package V3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成 Deploy a Package 收尾：配置改写、安装策略、非 NuGet archive 包源全部默认常显且真实生效，不破坏 V1/V2。

**Architecture:** 前端 model/editor 常显写入 canonical action properties；Calamari 复用/补齐配置 rewrite steps 并挂到 package 安装 staging 管线；`PackageInstallationCoordinator` 扩展 skip/purge/preserve/retention/current/rollback；`PackageAcquisitionService` 放开可下载 archive 源并硬拒绝 Docker/Helm。实施分 W1–W4，不砍需求。

**Tech Stack:** React + TypeScript + Ant Design + Vitest；.NET / xUnit + Shouldly；Calamari pipeline steps。

**Design:** `docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md`

## Global Constraints

- 工作区：后端 `/Users/nacho/Documents/GitHub/SolarifyDev/Squid`，前端 `/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb`；分支 `feature/deploy-package-v1`（或当前 feature 分支），**不创建 worktree**。
- Action type 固定：`Squid.TentaclePackage`。
- 继承 V1/V2：Server 下载后上传目标机、Release 固定版本、Tentacle/SSH 安装闭环、SHA-256、V2 step 控制字段语义不变。
- **全部 V3 能力默认常显**，不做 Configure features 开关体系。
- 必须做：Config Variables / Transforms / Substitute / Structured / 非 NuGet archive / purge-preserve / skip / retention / current+rollback。
- 明确不做：Configure features 真体系；步骤内选版本；Docker/Helm 静默当目录安装。
- 属性只写 canonical；Calamari 可读 IIS legacy fallback。
- 禁止 barrel exports；不生成/手改 `.d.ts`；不提交无关 `.env` / `pnpm-lock.yaml` / 用户无关 dirty。
- 始终简体中文；只做计划明确要求的事；允许为功能完整性做前后端配套改造。
- 真实 E2E 遇环境问题可暂停由用户处理。

### 属性契约（实现必须使用）

| 语义 | 属性 |
| --- | --- |
| Config Variables | `Squid.Action.ConfigurationVariables.Enabled` |
| Transforms | `Squid.Action.ConfigurationTransforms.Enabled` / `EnvironmentName` / `AdditionalTransforms` |
| Substitute | `Squid.Action.SubstituteInFiles.Enabled` / `TargetFiles` / `ShouldFailDeploymentOnSubstitutionFails` |
| Structured/JSON | `Squid.Action.JsonConfigVariables.Enabled` / `Targets`（canonical；兼容读 `StructuredConfigurationVariables.*` 与 IIS legacy） |
| Purge | `Squid.Action.Package.PurgeBeforeInstall` |
| Preserve | `Squid.Action.Package.PreservePaths` |
| Skip | `Squid.Action.Package.SkipIfAlreadyInstalled` |
| Retention | `Squid.Action.Package.RetentionCount` |
| Current | `Squid.Action.Package.UseCurrentPointer` |
| Rollback | `Squid.Action.Package.RollbackOnFailure` |

布尔写入与 Calamari `IsEnabled` 对齐：比较时 `string.Equals(raw, "True", OrdinalIgnoreCase)`；前端可写 `True`/`False`。

### 安装管线顺序（目标侧）

```text
hash verify
→ skip check（若 skip 且已安装同版本 → success 退出）
→ extract to staging
→ Custom：copy existing final → staging
→ SubstituteInFiles
→ ConfigurationTransforms
→ ConfigurationVariables
→ StructuredConfigVariables
→ purge/preserve 计算最终文件集
→ commit staging → final（backup）
→ current 指针 / retention
→ PreDeploy / PostDeploy
```

---

## File Structure

### 前端（SquidWeb）
- Modify: `src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts`
- Create: `src/pages/project-detail/step-editors/deploy-package/deploy-package-config-model.ts`（可选拆分；若保持单文件也可）
- Create: `src/pages/project-detail/step-editors/deploy-package/deploy-package-install-options-model.ts`（可选）
- Modify: `src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx`
- Modify: `src/pages/project-detail/step-editors/deploy-package/test.ts`

### 后端 / Calamari（Squid）
- Modify: `src/Squid.Message/Constants/SpecialVariables.cs`
- Create: `src/Squid.Calamari/Commands/Configuration/ConfigurationVariablesStep.cs`
- Create: `src/Squid.Calamari/Commands/Configuration/ConfigurationVariablesVariableNames.cs`（或同文件 public names）
- Modify: `src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs`
- Modify: `src/Squid.Calamari/Commands/Package/DeployPackageCommand.cs`（如需传更多变量）
- Modify: `src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionService.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Packages/HttpPackageContentFetcher.cs`（按 feed 下载）
- Modify: `src/Squid.Core/Services/DeploymentExecution/Handlers/DeployPackageActionHandler.cs`（可选 feed 校验）
- Tests:
  - `tests/Squid.Calamari.Tests/Calamari/Commands/Configuration/ConfigurationVariablesStepTests.cs`
  - `tests/Squid.Calamari.Tests/Calamari/Package/PackageInstallationCoordinatorTests.cs`（扩展）
  - `tests/Squid.UnitTests/Services/Deployments/Execution/PackageAcquisitionServiceTests.cs` 或现有 acquisition 测试扩展
  - `SquidWeb/.../deploy-package/test.ts`

### 文档
- Modify: `docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md`（W4 完成状态）
- 本文件跟踪勾选

---

### Task 1: 前端 model — 配置改写字段

**Files:**
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts`
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/test.ts`

**Interfaces:**
- Consumes: 现有 `DeployPackageFormState` / normalize / build / validate
- Produces:

```ts
export const PackageConfigProperties = {
  configurationVariablesEnabled: 'Squid.Action.ConfigurationVariables.Enabled',
  configurationTransformsEnabled: 'Squid.Action.ConfigurationTransforms.Enabled',
  configurationTransformsEnvironmentName: 'Squid.Action.ConfigurationTransforms.EnvironmentName',
  configurationTransformsAdditional: 'Squid.Action.ConfigurationTransforms.AdditionalTransforms',
  substituteInFilesEnabled: 'Squid.Action.SubstituteInFiles.Enabled',
  substituteInFilesTargetFiles: 'Squid.Action.SubstituteInFiles.TargetFiles',
  substituteFailOnUnresolved: 'Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails',
  jsonConfigVariablesEnabled: 'Squid.Action.JsonConfigVariables.Enabled',
  jsonConfigVariablesTargets: 'Squid.Action.JsonConfigVariables.Targets',
} as const

// DeployPackageFormState 新增：
// configurationVariablesEnabled: boolean
// configurationTransformsEnabled: boolean
// configurationTransformsEnvironmentName: string  // default '#{Squid.Environment.Name}'
// additionalTransforms: string
// substituteInFilesEnabled: boolean
// substituteInFilesTargetFiles: string
// substituteFailOnUnresolved: boolean
// structuredConfigEnabled: boolean
// structuredConfigTargets: string
```

- [ ] **Step 1: 写失败测试**

在 `test.ts` 增加：

```ts
it('normalizes and builds configuration rewrite properties', () => {
  const existing = {
    id: 9,
    processId: 1,
    stepOrder: 1,
    name: 'Deploy Web',
    stepType: 'DeployPackage',
    condition: 'Success',
    startTrigger: 'StartAfterPrevious',
    packageRequirement: 'LetSquidDecide',
    isDisabled: false,
    isRequired: false,
    createdAt: new Date().toISOString(),
    properties: [
      { id: 1, stepId: 9, propertyName: 'Squid.Action.TargetRoles', propertyValue: 'web' },
    ],
    actions: [{
      id: 1,
      stepId: 9,
      actionOrder: 0,
      name: 'Deploy Web',
      actionType: 'Squid.TentaclePackage',
      workerPoolId: null,
      isDisabled: false,
      isRequired: false,
      canBeUsedForProjectVersioning: true,
      createdAt: new Date().toISOString(),
      properties: [
        { id: 1, actionId: 1, propertyName: 'Squid.Action.Package.FeedId', propertyValue: '3' },
        { id: 2, actionId: 1, propertyName: 'Squid.Action.Package.PackageId', propertyValue: 'Acme.Web' },
        { id: 3, actionId: 1, propertyName: 'Squid.Action.ConfigurationVariables.Enabled', propertyValue: 'True' },
        { id: 4, actionId: 1, propertyName: 'Squid.Action.ConfigurationTransforms.Enabled', propertyValue: 'True' },
        { id: 5, actionId: 1, propertyName: 'Squid.Action.ConfigurationTransforms.EnvironmentName', propertyValue: '#{Squid.Environment.Name}' },
        { id: 6, actionId: 1, propertyName: 'Squid.Action.SubstituteInFiles.Enabled', propertyValue: 'True' },
        { id: 7, actionId: 1, propertyName: 'Squid.Action.SubstituteInFiles.TargetFiles', propertyValue: 'appsettings.json' },
        { id: 8, actionId: 1, propertyName: 'Squid.Action.JsonConfigVariables.Enabled', propertyValue: 'True' },
        { id: 9, actionId: 1, propertyName: 'Squid.Action.JsonConfigVariables.Targets', propertyValue: '**/appsettings.json' },
        { id: 10, actionId: 1, propertyName: 'Custom.Keep', propertyValue: 'yes' },
      ],
      environments: [],
      excludedEnvironments: [],
      channels: [],
    }],
  } as any

  const form = normalizeDeployPackageForm(existing)
  expect(form.configurationVariablesEnabled).toBe(true)
  expect(form.configurationTransformsEnabled).toBe(true)
  expect(form.substituteInFilesEnabled).toBe(true)
  expect(form.structuredConfigEnabled).toBe(true)

  const dto = buildDeployPackageStepDto({ form, processId: 1, existingStep: existing })
  const actionProps = Object.fromEntries(dto.actions[0].properties.map((p) => [p.propertyName, p.propertyValue]))
  expect(actionProps['Squid.Action.ConfigurationVariables.Enabled']).toBe('True')
  expect(actionProps['Squid.Action.JsonConfigVariables.Targets']).toBe('**/appsettings.json')
  expect(actionProps['Custom.Keep']).toBe('yes')
})
```

- [ ] **Step 2: 跑测确认失败**

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
```

Expected: FAIL（新字段不存在）

- [ ] **Step 3: 实现 model**

- 扩展 `DeployPackageFormState` 与默认值（全 false / env default / 空字符串）
- `normalizeBoolean` 已有则复用；识别 `True`/`true`/`1`
- `build`：仅 enabled 时写对应 props；transforms env 在 transforms enabled 时写入；known action set 包含全部 config props
- 保留未知 action props

- [ ] **Step 4: 跑测确认通过**

```bash
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
```

Expected: PASS

- [ ] **Step 5: Commit（SquidWeb）**

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
git add src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts \
        src/pages/project-detail/step-editors/deploy-package/test.ts
git commit -m "$(cat <<'EOF'
feat(deploy-package): model config rewrite fields for v3

Persist configuration variables, transforms, substitute-in-files and
structured config properties on Deploy a Package steps.
EOF
)"
```

---

### Task 2: 前端 editor — 配置改写常显分区

**Files:**
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx`

**Interfaces:**
- Consumes: Task 1 form 字段
- Produces: Installation Directory 之后常显四区；保存走 validate+build

- [ ] **Step 1: 实现 UI 分区**

顺序插入在 Installation Directory 与 Execution Location 之间：

1. `.NET Configuration Variables` — checkbox
2. `.NET Configuration Transforms` — checkbox + env Input + Additional TextArea
3. `Substitute Variables in Files` — checkbox + targets TextArea + fail-on-unresolved checkbox
4. `Structured Configuration Variables` — checkbox + targets TextArea

状态全部绑 Task 1 字段；`handleSave` form 组装包含新字段。

对齐 IIS 文案风格，但不依赖 `enabledFeatures`。

- [ ] **Step 2: 验证**

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
pnpm typecheck
rg -n "Configuration Variables|Configuration Transforms|Substitute Variables|Structured Configuration" \
  src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx
```

Expected: tests/typecheck PASS；rg 命中四区标题

- [ ] **Step 3: Commit（SquidWeb）**

```bash
git add src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx
git commit -m "$(cat <<'EOF'
feat(deploy-package): show config rewrite sections by default

Add always-visible configuration variables, transforms, substitute and
structured-config controls to the Deploy a Package editor.
EOF
)"
```

---

### Task 3: SpecialVariables + ConfigurationVariablesStep

**Files:**
- Modify: `src/Squid.Message/Constants/SpecialVariables.cs`
- Create: `src/Squid.Calamari/Commands/Configuration/ConfigurationVariablesStep.cs`
- Create: `tests/Squid.Calamari.Tests/Calamari/Commands/Configuration/ConfigurationVariablesStepTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class ConfigurationVariablesVariableNames
{
    public const string Enabled = "Squid.Action.ConfigurationVariables.Enabled";
    public static class Legacy
    {
        public const string Enabled = "Squid.Action.IISWebSite.ConfigurationVariables.Enabled";
    }
}

// ConfigurationVariablesStep : ExecutionStep<RunScriptCommandContext> 或 package context 可用的同类
// IsEnabled: True ignore-case on canonical then legacy
// Execute: scan working dir *.config; replace appSettings/applicationSettings/connectionStrings matching variable names
```

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void IsEnabled_True_WhenCanonicalEnabled()
{
    var ctx = BuildCtx(workingDir: CreateTempDir(), variables: new VariableSet
    {
        // set Enabled True + a variable AppName=Hello
    });
    // 写入最小 Web.config 含 <appSettings><add key="AppName" value="old"/>
    new ConfigurationVariablesStep().IsEnabled(ctx).ShouldBeTrue();
}

[Fact]
public void Execute_ReplacesAppSettingsByVariableName()
{
    // arrange Web.config AppName=old, variable AppName=Hello
    // act ExecuteAsync
    // assert file contains value="Hello"
}
```

（测试文件路径按项目现有 Calamari test 模式：创建临时目录 + VariableSet）

- [ ] **Step 2: 跑测 FAIL**

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/Squid
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~ConfigurationVariablesStepTests"
```

Expected: FAIL

- [ ] **Step 3: 实现 step**

参考 `ConfigurationTransformsStep` 结构与 IIS 语义：

- 仅处理 `.config` XML
- 匹配：
  - `//appSettings/add[@key=...]`
  - `//connectionStrings/add[@name=...]`
  - `//applicationSettings//setting[@name=...]`（若存在 value 节点则写 value）
- 变量查找：VariableSet 按 name 精确匹配（ignore-case）
- 解析失败：抛错（除非变量 `Squid.Action.Package.IgnoreVariableReplacementErrors`=`True` 则 warn skip）

- [ ] **Step 4: 跑测 PASS**

```bash
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~ConfigurationVariablesStepTests"
```

- [ ] **Step 5: Commit（Squid）**

```bash
git add src/Squid.Message/Constants/SpecialVariables.cs \
        src/Squid.Calamari/Commands/Configuration/ConfigurationVariablesStep.cs \
        tests/Squid.Calamari.Tests/Calamari/Commands/Configuration/ConfigurationVariablesStepTests.cs
git commit -m "$(cat <<'EOF'
feat(calamari): add .NET configuration variables rewrite step

Replace matching appSettings and connectionStrings entries from deployment
variables when ConfigurationVariables is enabled.
EOF
)"
```

---

### Task 4: 将 rewrite 管线挂入 package 安装

**Files:**
- Modify: `src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs`
- Modify: `tests/Squid.Calamari.Tests/Calamari/Package/PackageInstallationCoordinatorTests.cs`

**Interfaces:**
- Consumes: request.Variables 上的 rewrite enable flags
- Produces: staging 目录在 commit 前完成 rewrite

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Install_WithSubstituteEnabled_RewritesFileInFinalDirectory()
{
    // package zip 含 appsettings.json with #{Greeting}
    // variables: Substitute enabled + Greeting=Hi + TargetFiles=appsettings.json
    // InstallAsync
    // finalDir/appsettings.json contains Hi
}
```

- [ ] **Step 2: 跑测 FAIL**

```bash
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~PackageInstallationCoordinatorTests"
```

- [ ] **Step 3: 实现**

在 extract 到 staging 之后、`CommitDirectory` 之前：

```csharp
await RunConfigRewritePipelineAsync(stagingDir, request.Variables, ct);
```

实现 `RunConfigRewritePipelineAsync`：

1. 构造最小 `RunScriptCommandContext`（WorkingDirectory=stagingDir, Variables=request.Variables）
2. 顺序执行：
   - `new SubstituteInFilesStep()`
   - `new ConfigurationTransformsStep()`
   - `new ConfigurationVariablesStep()`
   - `new StructuredConfigVariablesStep()`
3. 各 step 自行 `IsEnabled` 门闩

若 context 类型不兼容，抽取 shared execution context 或为 package 增加 adapter——优先最小改动让现有 step 可跑。

- [ ] **Step 4: 跑测 PASS** + 现有 package tests

```bash
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~PackageInstallation|FullyQualifiedName~ConfigurationVariables|FullyQualifiedName~SubstituteInFiles|FullyQualifiedName~ConfigurationTransforms"
```

- [ ] **Step 5: Commit**

```bash
git add src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs \
        tests/Squid.Calamari.Tests/Calamari/Package/PackageInstallationCoordinatorTests.cs
git commit -m "$(cat <<'EOF'
feat(calamari): run config rewrite pipeline before package commit

Apply substitute, transforms, configuration variables and structured
config rewrites on the staging directory during deploy-package installs.
EOF
)"
```

---

### Task 5: 前端 model + editor — Installation Options

**Files:**
- Modify: `SquidWeb/.../deploy-package-model.ts`
- Modify: `SquidWeb/.../DeployPackageEditor.tsx`
- Modify: `SquidWeb/.../test.ts`

**Interfaces:**

```ts
export const PackageInstallOptionProperties = {
  purgeBeforeInstall: 'Squid.Action.Package.PurgeBeforeInstall',
  preservePaths: 'Squid.Action.Package.PreservePaths',
  skipIfAlreadyInstalled: 'Squid.Action.Package.SkipIfAlreadyInstalled',
  retentionCount: 'Squid.Action.Package.RetentionCount',
  useCurrentPointer: 'Squid.Action.Package.UseCurrentPointer',
  rollbackOnFailure: 'Squid.Action.Package.RollbackOnFailure',
} as const

// form:
// purgeBeforeInstall: boolean
// preservePaths: string
// skipIfAlreadyInstalled: boolean
// retentionCount: number // default 0
// useCurrentPointer: boolean
// rollbackOnFailure: boolean
```

- [ ] **Step 1: 测试 normalize/build 安装选项**

```ts
it('normalizes and builds installation option properties', () => {
  // existing props purge True, retention 3, skip True
  // expect form fields
  // build writes Squid.Action.Package.* 
})
```

- [ ] **Step 2: FAIL → 实现 model → PASS**

- [ ] **Step 3: Editor 增加 `Installation Options` 分区**

字段：
- Skip if already installed
- Purge files not in package + Preserve paths textarea
- Keep N previous versions (number, Versioned 提示)
- Maintain current pointer (Versioned)
- Rollback on failure

Custom 模式：retention/current 显示说明 no-op（仍可保存字段，执行 no-op）。

- [ ] **Step 4: typecheck + vitest**

```bash
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
pnpm typecheck
```

- [ ] **Step 5: Commit（SquidWeb）**

```bash
git commit -m "$(cat <<'EOF'
feat(deploy-package): add installation options for v3 policies

Expose skip, purge/preserve, retention, current pointer and rollback
controls on the Deploy a Package editor.
EOF
)"
```

---

### Task 6: PackageInstallationCoordinator — 安装策略

**Files:**
- Modify: `src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs`
- Modify: `tests/Squid.Calamari.Tests/Calamari/Package/PackageInstallationCoordinatorTests.cs`

**语义（必须测）：**

| 选项 | 行为 |
| --- | --- |
| Skip | final 存在且 `.squid-installed.json`（或等价标记）记录相同 PackageId+Version → 直接 success，不 extract/rewrite/commit |
| Purge | commit 后 final 中删除不在 package 文件集中的路径；PreservePaths glob 排除 |
| Retention | Versioned：父目录下仅保留最近 N 个版本目录（N=`RetentionCount`；0=不清理） |
| Current | Versioned：`{packageRoot}/current` 指向当前版本目录（目录联接/指针文件二选一，实现选平台可靠方案并单测） |
| Rollback | 失败时：恢复 backup；若启用 current 则指回 previous |

安装成功后写标记文件到 final：

```json
{"packageId":"...","version":"...","installedAtUtc":"..."}
```

- [ ] **Step 1: 写失败测试（至少 4 个）**

```csharp
[Fact] public async Task Skip_WhenSameVersionInstalled_DoesNotReextract() { ... }
[Fact] public async Task Purge_RemovesFilesNotInPackage_ButKeepsPreserved() { ... }
[Fact] public async Task Retention_KeepsOnlyNVersions() { ... }
[Fact] public async Task CurrentPointer_UpdatesOnSuccess_AndRollbackRestoresPrevious() { ... }
```

- [ ] **Step 2: FAIL → 实现 → PASS**

```bash
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~PackageInstallationCoordinatorTests"
```

- [ ] **Step 3: Commit**

```bash
git commit -m "$(cat <<'EOF'
feat(calamari): implement package install skip purge retention and current

Honor Deploy a Package installation policy properties during durable
package commits, including rollback helpers for failed switches.
EOF
)"
```

---

### Task 7: 非 NuGet 包源 — acquisition + editor

**Files:**
- Modify: `src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionService.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Packages/HttpPackageContentFetcher.cs`（如需）
- Modify: `SquidWeb/.../DeployPackageEditor.tsx`（去掉 NuGet-only filter；不支持类型校验）
- Modify: `SquidWeb/.../deploy-package-model.ts`（`isSupportedDeployPackageFeedType`）
- Tests: unit acquisition + frontend validate

**支持矩阵：**

| FeedType 关键词 | 行为 |
| --- | --- |
| NuGet | 支持（现有） |
| GitHub | 支持下载 release asset archive |
| Maven / 通用 HTTP archive | 支持（fetcher 已能下则复用） |
| Docker / container | **拒绝** 明确错误 |
| Helm | **拒绝** 明确错误 |

- [ ] **Step 1: 后端失败测试**

```csharp
[Theory]
[InlineData("Docker")]
[InlineData("Helm")]
public async Task AcquireAsync_UnsupportedFeedType_Throws(string feedType) { ... }

[Fact]
public async Task AcquireAsync_GitHubFeed_DoesNotThrowNuGetOnlyGuard() { ... }
```

- [ ] **Step 2: 实现**

替换 `PackageAcquisitionService` 中 NuGet-only 拒绝逻辑：

```csharp
if (IsUnsupportedPackageFeed(feedType))
    throw new InvalidOperationException($"Feed type '{feed.FeedType}' cannot be installed by Deploy a Package. Use an archive-capable feed (NuGet/GitHub/HTTP).");
```

本地文件名使用真实扩展名（从 URL/content-type/package id 推断，fallback `.zip`）。

- [ ] **Step 3: 前端**

```ts
export function isUnsupportedDeployPackageFeedType(feedType?: string | null): boolean {
  const t = (feedType ?? '').toLowerCase()
  return t.includes('docker') || t.includes('helm') || t.includes('container')
}
```

- 列表显示全部 feeds
- 选择 unsupported → `validateDeployPackageForm` 失败并提示
- 搜索仍按 feedId 调用现有 search API

- [ ] **Step 4: 测试 PASS**

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/Squid
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~PackageAcquisition|FullyQualifiedName~HttpPackageContentFetcher|FullyQualifiedName~DeployPackage"
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
pnpm typecheck
```

- [ ] **Step 5: Commit（分仓库）**

Squid:

```bash
git commit -m "$(cat <<'EOF'
feat(deploy-package): support archive feeds and reject container charts

Allow Deploy a Package acquisition from archive-capable non-NuGet feeds
while failing fast for Docker and Helm feed types.
EOF
)"
```

SquidWeb:

```bash
git commit -m "$(cat <<'EOF'
feat(deploy-package): accept non-nuget feeds with installability checks

Show all external feeds in the package editor and block unsupported
Docker/Helm selections at validation time.
EOF
)"
```

---

### Task 8: 联调回归 + 完成定义

**Files:** 仅修复缺口时改代码；更新 design 状态

- [ ] **Step 1: 后端回归**

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/Squid
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~DeployPackage|FullyQualifiedName~AcquirePackages|FullyQualifiedName~ConfigurationVariables|FullyQualifiedName~StepRetry|FullyQualifiedName~StepTimeout"
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~Configuration|FullyQualifiedName~Substitute|FullyQualifiedName~Structured"
```

Expected: PASS

- [ ] **Step 2: 前端回归**

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
pnpm typecheck
```

Expected: PASS（lint 若仍缺 `@sj-distributor/eslint-config` 记环境问题）

- [ ] **Step 3: 浏览器清单（手工）**

1. 配置四区常显，开关保存回显  
2. Installation Options 保存回显  
3. 非 NuGet feed 可选；Docker/Helm 有明确错误  
4. V2 控制字段不回归  
5. Release 仍选版本  
6. Configure features 无真功能（壳可保留）

- [ ] **Step 4: 更新 design 状态**

在 `docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md` §13：

```markdown
- design：implemented
- plan：completed
```

- [ ] **Step 5: Commit 修复项与文档（如有）**

```bash
git status --short
# 有修复则提交；无则跳过
```

---

## Spec Coverage

| Design 要求 | Task |
| --- | --- |
| Config Variables | Task 1–4 |
| Transforms | Task 1–4 |
| Substitute | Task 1–4 |
| Structured/JSON | Task 1–4 |
| 安装策略 7–10 | Task 5–6 |
| 非 NuGet 6 | Task 7 |
| 常显 UI | Task 2、5 |
| 不做 feature 体系 | 全程 |
| V1/V2 不回归 | Task 8 |
| 测试/验收 | 各 Task + Task 8 |

## Placeholder Scan

- 无 TBD / “similar to Task N” 无展开说明
- 属性名、命令、Expected FAIL/PASS、commit message 齐全

## Type Consistency

- Structured canonical：`Squid.Action.JsonConfigVariables.*`（与现有 Calamari `StructuredConfigVariableNames` 一致）
- Config Variables canonical：`Squid.Action.ConfigurationVariables.Enabled`
- Package 策略均在 `Squid.Action.Package.*`
- Action type：`Squid.TentaclePackage`

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-15-deploy-a-package-v3.md`.

**Two execution options:**

1. **Subagent-Driven（推荐）** — 每 Task 新 subagent，Task 间审查  
2. **Inline Execution** — 本会话连续执行并设检查点  

Which approach?
