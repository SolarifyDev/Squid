# Deploy a Package V2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `Deploy a Package` 提升为与 Run Script 同级的完整 step：编辑器可见分区齐全，通用控制字段真实保存并驱动执行语义，Configure features 仅按钮壳，最后启动 V3 计划。

**Architecture:** 以前端 model/editor 对齐 Run Script 的完整控制字段写入约定；后端补齐通用属性常量、Timeout 秒数解析、Retries 执行接入，确保 `Squid.TentaclePackage` 与现有 pipeline 控制面一致。V1 package 安装闭环不改语义。

**Tech Stack:** React + TypeScript + Ant Design + Vitest；.NET / xUnit + Shouldly + Moq。

## Global Constraints

- 工作区：后端 `/Users/nacho/Documents/GitHub/SolarifyDev/Squid`，前端 `/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb`；分支 `feature/deploy-package-v1`（或当前 feature 分支），不创建 worktree。
- Action type 固定：`Squid.TentaclePackage`。
- 继承 V1：外部 NuGet only、Server 下载、Release 固定版本、Tentacle/SSH 安装闭环、SHA-256。
- Configure features：仅按钮壳，不实现 features 内容。
- 不做：.NET Configuration Variables/Transforms、purge/preserve、skip-if-installed、retention、自动回滚、非 NuGet 源。
- 允许为功能完整性做前后端配套改造，不以最小 diff 为约束。
- 禁止 barrel exports；不生成/手改 `.d.ts`；不提交无关 `.env` / 用户 `pnpm-lock.yaml` 本地改动。
- 始终简体中文回复；只做计划明确要求的事。
- 计划结束必须包含“启动 V3 计划”任务。

### 关键现状（实现前必读）

| 字段 | 前端 Run Script 写法 | 后端当前读取 | V2 必须处理 |
| --- | --- | --- | --- |
| StartTrigger | `step.startTrigger` | `StepBatcher` 已读 | 前端写入即可 |
| MaxParallelism | step prop `Squid.Step.MaxParallelism` 整数字符串 | `TargetParallelExecutor.ParseMaxParallelism` | 前端写入即可 |
| RunOnServer | step prop `Squid.Action.RunOnServer` true/false | `RunOnServerEvaluator.IsRunOnServer` | 前端按 Execution Location 写入 |
| Timeout | Run Script 写**秒数字符串** `"30"` | `StepTimeoutParser` 用 `TimeSpan.TryParse`，`"30"` 会被当成 30 天 | **必须修 parser 优先按秒解析** |
| RetriesEnabled/Count | Run Script 写 `Squid.Step.RetriesEnabled` / `RetriesCount` | pipeline **当前未消费** | **必须接入 step 失败重试** |
| ExecutionLocation | action prop `Squid.Action.Script.ExecutionLocation` | 主要靠 RunOnServer 分流 | package 同步写入；Worker 不可用时显式禁用/报错 |

---

## File Structure

### 前端
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts`
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx`
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/test.ts`
- Modify: `SquidWeb/src/pages/project-detail/DeploymentProcess.tsx`（featured）

### 后端
- Modify: `src/Squid.Message/Constants/SpecialVariables.cs`（Retries 常量）
- Modify: `src/Squid.Core/Services/DeploymentExecution/Filtering/StepTimeoutParser.cs`
- Create: `src/Squid.Core/Services/DeploymentExecution/Filtering/StepRetryPolicy.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs`（step/action 重试）
- Modify/Create tests:
  - `tests/Squid.UnitTests/Services/Deployments/Execution/StepTimeoutParserTests.cs`
  - `tests/Squid.UnitTests/Services/Deployments/Execution/StepRetryPolicyTests.cs`
  - pipeline/phase 相关测试（按现有结构扩展）

### 文档
- Create: `docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md`（启动稿）
- Create: `docs/superpowers/plans/2026-07-15-deploy-a-package-v3.md`（启动稿/占位可评审计划入口）

---

### Task 1: 扩展 deploy-package model（完整控制字段）

**Files:**
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts`
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/test.ts`

**Interfaces:**
- Consumes: 现有 `DeployPackageFormState` / `buildDeployPackageStepDto` / `normalizeDeployPackageForm` / `validateDeployPackageForm`
- Produces:

```ts
export type ExecutionLocationMode = 'WorkerPool' | 'WorkerPoolForRoles' | 'DeploymentTarget'
export type StartTriggerMode = 'StartAfterPrevious' | 'StartWithPrevious'

export const StepControlProperties = {
  runOnServer: 'Squid.Action.RunOnServer',
  executionLocation: 'Squid.Action.Script.ExecutionLocation',
  retriesEnabled: 'Squid.Step.RetriesEnabled',
  retriesCount: 'Squid.Step.RetriesCount',
  timeout: 'Squid.Step.Timeout',
  timeoutMinutesLegacy: 'Squid.Step.TimeoutInMinutes',
  maxParallelism: 'Squid.Step.MaxParallelism',
  targetRoles: 'Squid.Action.TargetRoles',
  conditionExpression: 'Squid.Step.ConditionExpression',
} as const

export interface DeployPackageFormState {
  stepName: string
  feedId: number | null
  packageId: string
  targetRoles: string[]
  installationDirectoryMode: InstallationDirectoryMode
  customInstallationDirectory: string
  executionLocation: ExecutionLocationMode
  startTrigger: StartTriggerMode
  retriesEnabled: boolean
  retryCount: number // 1..3
  maxParallelism: number // 0 = off
  timeoutSeconds: number // 0 = never
  conditions: StepConditionsValue
}
```

- [ ] **Step 1: 写失败测试**

在 `test.ts` 增加：

```ts
it('normalizes and builds full step control properties', () => {
  const existing = {
    id: 9,
    processId: 1,
    stepOrder: 1,
    name: 'Deploy Web',
    stepType: 'DeployPackage',
    condition: 'Success',
    startTrigger: 'StartWithPrevious',
    packageRequirement: 'LetSquidDecide',
    isDisabled: false,
    isRequired: true,
    createdAt: new Date().toISOString(),
    properties: [
      { id: 1, stepId: 9, propertyName: 'Squid.Action.TargetRoles', propertyValue: 'web' },
      { id: 2, stepId: 9, propertyName: 'Squid.Action.RunOnServer', propertyValue: 'false' },
      { id: 3, stepId: 9, propertyName: 'Squid.Step.RetriesEnabled', propertyValue: 'true' },
      { id: 4, stepId: 9, propertyName: 'Squid.Step.RetriesCount', propertyValue: '2' },
      { id: 5, stepId: 9, propertyName: 'Squid.Step.Timeout', propertyValue: '45' },
      { id: 6, stepId: 9, propertyName: 'Squid.Step.MaxParallelism', propertyValue: '3' },
    ],
    actions: [{
      id: 1,
      stepId: 9,
      actionOrder: 0,
      name: 'Deploy Web',
      actionType: 'Squid.TentaclePackage',
      workerPoolId: null,
      isDisabled: false,
      isRequired: true,
      canBeUsedForProjectVersioning: true,
      createdAt: new Date().toISOString(),
      properties: [
        { id: 1, actionId: 1, propertyName: 'Squid.Action.Package.FeedId', propertyValue: '3' },
        { id: 2, actionId: 1, propertyName: 'Squid.Action.Package.PackageId', propertyValue: 'Acme.Web' },
        { id: 3, actionId: 1, propertyName: 'Squid.Action.Script.ExecutionLocation', propertyValue: 'DeploymentTarget' },
        { id: 4, actionId: 1, propertyName: 'Custom.Unknown', propertyValue: 'keep-me' },
      ],
      environments: [],
      excludedEnvironments: [],
      channels: [],
    }],
  } as any

  const form = normalizeDeployPackageForm(existing)
  expect(form.executionLocation).toBe('DeploymentTarget')
  expect(form.startTrigger).toBe('StartWithPrevious')
  expect(form.retriesEnabled).toBe(true)
  expect(form.retryCount).toBe(2)
  expect(form.timeoutSeconds).toBe(45)
  expect(form.maxParallelism).toBe(3)

  form.executionLocation = 'DeploymentTarget'
  form.retriesEnabled = true
  form.retryCount = 3
  form.timeoutSeconds = 90
  form.maxParallelism = 2

  const dto = buildDeployPackageStepDto({ form, processId: 1, existingStep: existing })
  const stepProps = Object.fromEntries(dto.properties.map((p) => [p.propertyName, p.propertyValue]))
  const actionProps = Object.fromEntries(dto.actions[0].properties.map((p) => [p.propertyName, p.propertyValue]))

  expect(dto.startTrigger).toBe('StartWithPrevious')
  expect(stepProps['Squid.Action.RunOnServer']).toBe('false')
  expect(stepProps['Squid.Step.RetriesEnabled']).toBe('true')
  expect(stepProps['Squid.Step.RetriesCount']).toBe('3')
  expect(stepProps['Squid.Step.Timeout']).toBe('90')
  expect(stepProps['Squid.Step.MaxParallelism']).toBe('2')
  expect(actionProps['Squid.Action.Script.ExecutionLocation']).toBe('DeploymentTarget')
  expect(actionProps['Custom.Unknown']).toBe('keep-me')
})

it('validates retries/timeout/rolling ranges', () => {
  const base: DeployPackageFormState = {
    stepName: 'Deploy Web',
    feedId: 3,
    packageId: 'Acme.Web',
    targetRoles: ['web'],
    installationDirectoryMode: 'Versioned',
    customInstallationDirectory: '',
    executionLocation: 'DeploymentTarget',
    startTrigger: 'StartAfterPrevious',
    retriesEnabled: true,
    retryCount: 9,
    maxParallelism: 0,
    timeoutSeconds: -1,
    conditions: emptyConditions,
  }
  expect(validateDeployPackageForm(base).ok).toBe(false)
})

it('maps WorkerPool execution location to RunOnServer=true', () => {
  const form: DeployPackageFormState = {
    stepName: 'Deploy Web',
    feedId: 3,
    packageId: 'Acme.Web',
    targetRoles: ['web'],
    installationDirectoryMode: 'Versioned',
    customInstallationDirectory: '',
    executionLocation: 'WorkerPool',
    startTrigger: 'StartAfterPrevious',
    retriesEnabled: false,
    retryCount: 1,
    maxParallelism: 0,
    timeoutSeconds: 0,
    conditions: emptyConditions,
  }
  const dto = buildDeployPackageStepDto({ form, processId: 1, existingStep: null })
  const stepProps = Object.fromEntries(dto.properties.map((p) => [p.propertyName, p.propertyValue]))
  expect(stepProps['Squid.Action.RunOnServer']).toBe('true')
})
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
```

Expected: FAIL（新字段不存在 / 断言失败）

- [ ] **Step 3: 实现 model**

按接口扩展 `DeployPackageFormState`，并实现：

```ts
export function normalizeBoolean(value?: string): boolean {
  if (!value?.trim()) return false
  const n = value.trim().toLowerCase()
  return n === 'true' || n === '1' || n === 'yes'
}

export function normalizeExecutionLocation(value?: string, runOnServer = false): ExecutionLocationMode {
  if (runOnServer) return 'WorkerPool'
  if (value === 'WorkerPool') return 'WorkerPool'
  if (value === 'WorkerPoolForRoles') return 'WorkerPoolForRoles'
  return 'DeploymentTarget'
}

export function parsePositiveInt(value: string | undefined, fallback: number): number {
  const n = Number.parseInt(value ?? '', 10)
  return Number.isFinite(n) ? n : fallback
}
```

`normalizeDeployPackageForm`：
- 读 `RunOnServer`、`ExecutionLocation`、`RetriesEnabled`、`RetriesCount`、`Timeout`（秒；若只有 legacy minutes 则 `* 60`）、`MaxParallelism`
- 默认：`executionLocation='DeploymentTarget'`，`retriesEnabled=false`，`retryCount=1`，`timeoutSeconds=0`，`maxParallelism=0`

`validateDeployPackageForm` 增加：
- retriesEnabled 时 retryCount ∈ [1,3]
- timeoutSeconds >= 0
- maxParallelism === 0 或 >= 1

`buildDeployPackageStepDto`：
- `startTrigger` 写入 step
- step props：TargetRoles、RunOnServer、Retries*、Timeout（仅 >0 写秒数字符串）、MaxParallelism（仅 >0）
- action props：ExecutionLocation
- known property 过滤集包含全部控制字段，避免重复堆叠
- 保留未知属性

Execution Location 映射：

```ts
const runOnServer = form.executionLocation === 'WorkerPool' || form.executionLocation === 'WorkerPoolForRoles'
// DeploymentTarget => RunOnServer=false
// WorkerPool / WorkerPoolForRoles => RunOnServer=true，并写 ExecutionLocation 原值
```

- [ ] **Step 4: 运行测试确认通过**

Run:

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
feat(deploy-package): expand step control model for v2 editor

Persist execution location, retries, timeout, rolling parallelism and
start trigger using the shared step property contracts.
EOF
)"
```

---

### Task 2: 重构 DeployPackageEditor 完整分区 + Featured

**Files:**
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx`
- Modify: `SquidWeb/src/pages/project-detail/DeploymentProcess.tsx`
- Modify: `SquidWeb/src/pages/project-detail/step-editors/deploy-package/test.ts`（如需组件级 smoke，可保持 model 测试为主）

**Interfaces:**
- Consumes: Task 1 的 form/model API
- Produces: 完整可见分区 UI；Configure features 按钮壳；Featured 可见

- [ ] **Step 1: 写/更新失败测试（模板 featured）**

在 `DeploymentProcess.tsx` 的模板定义可测性有限；用最小静态断言测试文件或直接在实现后人工+类型检查。  
推荐新增纯数据导出以便测试：

若不想改模板结构，本 Task 以 editor 行为 + typecheck 为主，并在 Step 4 用源码断言：

创建文件 `SquidWeb/src/pages/project-detail/step-editors/deploy-package/featured-template.test.ts` 不合适（模板在 DeploymentProcess）。  
直接修改 `DeploymentProcess.tsx` 后用：

```bash
rg -n "Deploy a Package|[\\s\\S]*featured: true" src/pages/project-detail/DeploymentProcess.tsx
```

作为检查命令。计划实现时给 package 模板加 `featured: true`。

- [ ] **Step 2: 重构 editor UI**

`DeployPackageEditor.tsx` 按以下顺序重建（对齐 Run Script）：

1. 页头：logo、标题、`Configure features` 按钮  
   - 点击 `message.info('Package features are not available in V2.')` 或 Modal 占位
2. Step Name
3. Package（Feed/Package，保留防抖搜索）
4. Target Roles（`TargetTagSelect`）
5. Installation Directory
6. Execution Location（三选一 radio；默认 DeploymentTarget）
7. Conditions（`StepConditionsSection` + afterRunCondition 放 Start Trigger）
8. Retries / Rolling Deployment / Time out

状态全部来自 Task 1 form 字段；保存走 `validateDeployPackageForm` + `buildDeployPackageStepDto`。

Worker 选项处理（按设计）：

```ts
// V2: 允许选择，但给出明确提示
// 若后续检测 package 不支持 worker，则禁用 WorkerPool 选项并说明原因
```

当前实现建议：
- UI 提供三选项
- 选择 WorkerPool / WorkerPoolForRoles 时显示 warning：  
  `Worker execution for package deploy is experimental; ensure worker targets support package installation.`
- 不静默改回 DeploymentTarget

- [ ] **Step 3: Featured 可见性**

`DeploymentProcess.tsx` 中 package 模板：

```ts
{
  id: 'package',
  title: 'Deploy a Package',
  actionType: 'Squid.TentaclePackage',
  bullets: ['Deploy the contents of a package to one or more deployment targets.'],
  categoryIds: ['package'],
  featured: true,
}
```

- [ ] **Step 4: 验证**

Run:

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
pnpm typecheck
pnpm lint
```

Expected: PASS  
另检查：

```bash
rg -n "featured: true" src/pages/project-detail/DeploymentProcess.tsx
rg -n "Configure features|Execution Location|Rolling Deployment|Time out|Retries" src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx
```

- [ ] **Step 5: Commit（SquidWeb）**

```bash
git add src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx \
        src/pages/project-detail/DeploymentProcess.tsx \
        src/pages/project-detail/step-editors/deploy-package/test.ts
git commit -m "$(cat <<'EOF'
feat(deploy-package): complete v2 step editor layout

Add full control sections, configure-features shell button, and featured
template visibility for Deploy a Package.
EOF
)"
```

---

### Task 3: Timeout 秒数解析契约修复

**Files:**
- Modify: `src/Squid.Core/Services/DeploymentExecution/Filtering/StepTimeoutParser.cs`
- Modify: `tests/Squid.UnitTests/Services/Deployments/Execution/StepTimeoutParserTests.cs`
- Modify: `src/Squid.Message/Constants/SpecialVariables.cs`（如需补 legacy 常量）

**Interfaces:**
- Consumes: step property `Squid.Step.Timeout`（前端写秒）
- Produces: `TimeSpan?` 正确秒级超时

- [ ] **Step 1: 写失败测试**

```csharp
[Theory]
[InlineData("30", 30)]
[InlineData("90", 90)]
[InlineData("0", null)]
public void ParseTimeout_NumericSeconds_ReturnsSeconds(string raw, int? expectedSeconds)
{
    var step = BuildStepWithTimeout(raw);
    var result = StepTimeoutParser.ParseTimeout(step);
    if (expectedSeconds is null)
        result.ShouldBeNull();
    else
        result.ShouldBe(TimeSpan.FromSeconds(expectedSeconds.Value));
}

[Fact]
public void ParseTimeout_TimeSpanString_StillSupported()
{
    var step = BuildStepWithTimeout("00:15:00");
    StepTimeoutParser.ParseTimeout(step).ShouldBe(TimeSpan.FromMinutes(15));
}

[Fact]
public void ParseTimeout_LegacyMinutesProperty_IsConvertedToSecondsSemanticsWhenOnlyLegacyPresent()
{
    var step = new DeploymentStepDto
    {
        Properties = new List<DeploymentStepPropertyDto>
        {
            new() { PropertyName = "Squid.Step.TimeoutInMinutes", PropertyValue = "2" }
        }
    };
    StepTimeoutParser.ParseTimeout(step).ShouldBe(TimeSpan.FromMinutes(2));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~StepTimeoutParserTests"
```

Expected: FAIL（`"30"` 当前被 `TimeSpan.TryParse` 解析成 30 天或行为不符）

- [ ] **Step 3: 实现 parser**

```csharp
public static class StepTimeoutParser
{
    public const string TimeoutInMinutesLegacy = "Squid.Step.TimeoutInMinutes";

    public static TimeSpan? ParseTimeout(DeploymentStepDto step)
    {
        var timeoutProp = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.Timeout);
        if (timeoutProp != null && !string.IsNullOrWhiteSpace(timeoutProp.PropertyValue))
            return ParseTimeoutValue(timeoutProp.PropertyValue);

        var legacy = step.Properties?.FirstOrDefault(p => p.PropertyName == TimeoutInMinutesLegacy);
        if (legacy != null && int.TryParse(legacy.PropertyValue, out var minutes) && minutes > 0)
            return TimeSpan.FromMinutes(minutes);

        return null;
    }

    private static TimeSpan? ParseTimeoutValue(string raw)
    {
        if (int.TryParse(raw, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

        if (TimeSpan.TryParse(raw, out var value) && value > TimeSpan.Zero)
            return value;

        return null;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~StepTimeoutParserTests"
```

Expected: PASS

- [ ] **Step 5: Commit（Squid）**

```bash
git add src/Squid.Core/Services/DeploymentExecution/Filtering/StepTimeoutParser.cs \
        tests/Squid.UnitTests/Services/Deployments/Execution/StepTimeoutParserTests.cs \
        src/Squid.Message/Constants/SpecialVariables.cs
git commit -m "$(cat <<'EOF'
fix(deploy): parse step timeout as seconds with timespan fallback

Align StepTimeoutParser with frontend second-based Timeout values while
keeping legacy TimeSpan and minutes-compatible inputs.
EOF
)"
```

---

### Task 4: Retries 常量 + 执行接入

**Files:**
- Modify: `src/Squid.Message/Constants/SpecialVariables.cs`
- Create: `src/Squid.Core/Services/DeploymentExecution/Filtering/StepRetryPolicy.cs`
- Create: `tests/Squid.UnitTests/Services/Deployments/Execution/StepRetryPolicyTests.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs`
- Create/Modify: phase 级测试（若已有 action failure 测试则扩展；否则新增 focused 测试）

**Interfaces:**
- Consumes: step props `Squid.Step.RetriesEnabled` / `Squid.Step.RetriesCount`
- Produces:

```csharp
public readonly record struct StepRetryPolicy(bool Enabled, int MaxAttempts)
{
    // MaxAttempts = 1 + retryCount when enabled; otherwise 1
    public static StepRetryPolicy FromStep(DeploymentStepDto step);
}
```

- [ ] **Step 1: 写失败测试**

```csharp
public class StepRetryPolicyTests
{
    [Fact]
    public void FromStep_Disabled_ReturnsSingleAttempt()
    {
        var step = new DeploymentStepDto { Properties = new() };
        var policy = StepRetryPolicy.FromStep(step);
        policy.Enabled.ShouldBeFalse();
        policy.MaxAttempts.ShouldBe(1);
    }

    [Fact]
    public void FromStep_EnabledCount2_Returns3Attempts()
    {
        var step = new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.RetriesEnabled, PropertyValue = "true" },
                new() { PropertyName = SpecialVariables.Step.RetriesCount, PropertyValue = "2" },
            }
        };
        var policy = StepRetryPolicy.FromStep(step);
        policy.Enabled.ShouldBeTrue();
        policy.MaxAttempts.ShouldBe(3);
    }

    [Theory]
    [InlineData("0", 2)] // clamp to at least 1 retry => attempts 2
    [InlineData("9", 4)] // clamp to max 3 retries => attempts 4
    public void FromStep_ClampsRetryCount(string raw, int expectedAttempts)
    {
        var step = new DeploymentStepDto
        {
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.RetriesEnabled, PropertyValue = "true" },
                new() { PropertyName = SpecialVariables.Step.RetriesCount, PropertyValue = raw },
            }
        };
        StepRetryPolicy.FromStep(step).MaxAttempts.ShouldBe(expectedAttempts);
    }
}
```

并增加一个执行层契约测试（若难直接测 private phase，至少测 policy + 源码/helper 调用点）：

优先在 `ExecuteStepsPhase` 可测路径中验证：当 action 执行抛错/失败且 retries enabled，会重试直到成功或耗尽。

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~StepRetryPolicyTests"
```

Expected: FAIL

- [ ] **Step 3: 实现常量与 policy**

`SpecialVariables.Step` 增加：

```csharp
public const string RetriesEnabled = "Squid.Step.RetriesEnabled";
public const string RetriesCount = "Squid.Step.RetriesCount";
```

`StepRetryPolicy.FromStep`：

```csharp
public static StepRetryPolicy FromStep(DeploymentStepDto step)
{
    var enabled = string.Equals(
        step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.RetriesEnabled)?.PropertyValue,
        "true", StringComparison.OrdinalIgnoreCase);

    if (!enabled) return new StepRetryPolicy(false, 1);

    var raw = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.RetriesCount)?.PropertyValue;
    var retries = int.TryParse(raw, out var n) ? n : 1;
    retries = Math.Clamp(retries, 1, 3);
    return new StepRetryPolicy(true, retries + 1);
}
```

在 `ExecuteSingleActionAsync`（或等价失败点）包一层 attempt 循环：

```csharp
var retryPolicy = StepRetryPolicy.FromStep(step);
for (var attempt = 1; attempt <= retryPolicy.MaxAttempts; attempt++)
{
    try
    {
        // existing execute path
        // on success break
        break;
    }
    catch (Exception ex) when (attempt < retryPolicy.MaxAttempts && IsRetryable(ex))
    {
        Log.Warning(ex, "[Deploy] Step {Step} action {Action} failed attempt {Attempt}/{Max}, retrying",
            step.Name, actionName, attempt, retryPolicy.MaxAttempts);
    }
}
```

`IsRetryable`：用户取消 / DeploymentAbortedException 不可重试；普通执行失败可重试。

- [ ] **Step 4: 运行测试确认通过**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~StepRetryPolicyTests|FullyQualifiedName~StepTimeoutParserTests|FullyQualifiedName~RunOnServerEvaluatorTests|FullyQualifiedName~TargetParallelExecutorTests.ParseMaxParallelism"
```

Expected: PASS

- [ ] **Step 5: Commit（Squid）**

```bash
git add src/Squid.Message/Constants/SpecialVariables.cs \
        src/Squid.Core/Services/DeploymentExecution/Filtering/StepRetryPolicy.cs \
        src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs \
        tests/Squid.UnitTests/Services/Deployments/Execution/StepRetryPolicyTests.cs
git commit -m "$(cat <<'EOF'
feat(deploy): honor step retry policy for action execution

Add shared RetriesEnabled/RetriesCount constants and apply clamped retry
attempts during step action execution failures.
EOF
)"
```

---

### Task 5: 控制字段联调验收 + V1 回归

**Files:**
- 可能小改前后端联调缺口
- 测试扩展：package step 写入后的 property 契约（可选后端 DTO roundtrip）

**Interfaces:**
- Consumes: Task 1-4
- Produces: V2 完成标准证据

- [ ] **Step 1: 后端回归**

Run:

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/Squid
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~DeployPackage|FullyQualifiedName~StepTimeoutParser|FullyQualifiedName~StepRetryPolicy|FullyQualifiedName~RunOnServer|FullyQualifiedName~ParseMaxParallelism|FullyQualifiedName~AcquirePackages"
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~PackageInstallationCoordinatorTests|FullyQualifiedName~DeployPackageCliCommandHandlerTests"
```

Expected: PASS

- [ ] **Step 2: 前端回归**

Run:

```bash
cd /Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb
pnpm exec vitest run src/pages/project-detail/step-editors/deploy-package/test.ts
pnpm typecheck
pnpm lint
```

Expected: PASS

- [ ] **Step 3: 浏览器验收清单（手工）**

1. Featured 中可见 Deploy a Package
2. Package 分类中也可见
3. 新建步骤：填 Feed/Package/Roles/Custom dir/Execution Location/Start Trigger/Retries/Rolling/Timeout
4. 保存后重新打开，全部字段回显
5. Configure features 按钮可点，不改保存结果
6. 创建 Release 仍出现该 package 并可选版本
7. 桌面/窄视口无重叠

- [ ] **Step 4: 记录结果并修缺口**

若发现 worker 路径会静默失败：  
在 editor 禁用 Worker 选项或保存时硬校验报错。  
若 timeout/retry 仍无效：回到 Task 3/4 修读取点。

- [ ] **Step 5: Commit 仅修复项（如有）**

```bash
# 仅在有代码修复时提交
git status --short
```

---

### Task 6: 启动 V3 计划（强制收尾）

**Files:**
- Create: `docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md`
- Create: `docs/superpowers/plans/2026-07-15-deploy-a-package-v3.md`
- Modify: `docs/superpowers/specs/2026-07-15-deploy-a-package-v2-design.md`（将第 12 节任务标为完成说明）

**Interfaces:**
- Consumes: V2 设计第 11/12 节
- Produces: V3 设计入口 + 计划入口

- [ ] **Step 1: 写 V3 设计启动稿**

`docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md` 至少包含：

```markdown
# Deploy a Package V3 设计（启动稿）

## 目标
在 V2 完整 step 控制面之上，补齐 Octopus 可见的 package 高级配置能力。

## 首批范围
1. .NET Configuration Variables
2. .NET Configuration Transforms
3. Configure features 从按钮壳升级为真实可配置能力

## 兼容策略
- 不破坏 V1 安装闭环
- 不破坏 V2 通用控制字段语义

## 状态
planning-entry：待 brainstorming 细化后进入 writing-plans。
```

- [ ] **Step 2: 写 V3 计划入口稿**

`docs/superpowers/plans/2026-07-15-deploy-a-package-v3.md`：

```markdown
# Deploy a Package V3 Implementation Plan (Entry)

> 入口文档。完整 task 拆分需在 V3 design 细化并批准后，用 writing-plans 重写。

**Goal:** 实现 .NET Configuration Variables / Transforms，并落地 Configure features 真能力。

**Next:**
1. brainstorming 细化 V3 范围与 Octopus 行为对齐点
2. 批准 design
3. writing-plans 重写本文件为可执行任务计划
```

- [ ] **Step 3: 更新 V2 design 第 12 节完成标记**

在 V2 design 第 12 节追加：

```markdown
### 结果（V2 完成后填写）

- V3 设计文档：`docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md`
- V3 计划入口：`docs/superpowers/plans/2026-07-15-deploy-a-package-v3.md`
- 状态：started
```

- [ ] **Step 4: Commit（Squid）**

```bash
git add docs/superpowers/specs/2026-07-15-deploy-a-package-v3-design.md \
        docs/superpowers/plans/2026-07-15-deploy-a-package-v3.md \
        docs/superpowers/specs/2026-07-15-deploy-a-package-v2-design.md
git commit -m "$(cat <<'EOF'
docs: start deploy-a-package v3 planning entry

Create V3 design/plan entry points for configuration variables, transforms,
and real configure-features work after V2 step-control parity.
EOF
)"
```

---

## Spec Coverage

| V2 设计要求 | Task |
| --- | --- |
| 完整编辑器分区 | Task 1 + Task 2 |
| Execution Location 真实保存/默认 Deployment Target | Task 1 + Task 2 |
| Start Trigger / Rolling / Timeout / Retries 真实可用 | Task 1 + Task 3 + Task 4 |
| Configure features 按钮壳 | Task 2 |
| Featured 可见 | Task 2 |
| 后端 pipeline 接入查漏补缺 | Task 3 + Task 4 + Task 5 |
| V1 不回归 | Task 5 |
| 启动 V3 计划 | Task 6 |

## Placeholder Scan

- 无 TBD / “similar to Task N”
- 所有代码步骤含具体接口、测试、命令、Expected FAIL/PASS、commit

## Type Consistency

- Action type：`Squid.TentaclePackage`
- ExecutionLocation：`WorkerPool | WorkerPoolForRoles | DeploymentTarget`
- Timeout property：`Squid.Step.Timeout` 存**秒**字符串；parser 优先秒
- Retries：`Squid.Step.RetriesEnabled` + `Squid.Step.RetriesCount`（1..3）
- MaxParallelism：`Squid.Step.MaxParallelism`
- RunOnServer：`Squid.Action.RunOnServer`
- StartTrigger：step.`startTrigger`

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-15-deploy-a-package-v2.md`.

**Two execution options:**

1. **Subagent-Driven (recommended)** — 每 Task 新 subagent，Task 间审查  
2. **Inline Execution** — 本会话 executing-plans 连续执行并设检查点  

Which approach?
