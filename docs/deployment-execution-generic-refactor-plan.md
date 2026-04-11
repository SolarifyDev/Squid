# Deployment Execution Layer — Generic Refactor Plan

**Status**: In Progress
**Owner**: Squid platform team
**Scope**: `src/Squid.Core/Services/DeploymentExecution/` (all transports + preview + pipeline)
**Driver**: Preview/Execute drift, SSH capability gap vs Octopus, handler-renders-script anti-pattern, file path contract conflict between SSH and Kubernetes.

---

## 1. Motivation

Squid's current deployment execution layer has accumulated four structural problems that cannot be fixed in isolation:

1. **Preview and Execute are drifting.** Preview (`DeploymentService.Preview.cs`) shares the *target/step/action filtering* utilities with the executor but does **not** resolve variables, does **not** call `IActionHandler.PrepareAsync`, and does **not** run any capability validation. The executor does all three. Every new feature added to execution risks breaking the preview promise "what you preview is what you deploy."

2. **Handlers render transport-specific artifacts.** `IActionHandler.PrepareAsync` currently returns `ActionExecutionResult { ScriptBody, Files, CalamariCommand }` — i.e. an already-rendered bash/PowerShell/kubectl script. This pushes transport knowledge into the handler (e.g. "I need to know SSH doesn't support nested files before I decide how to lay out `content/*.yaml`") and makes it impossible to implement a "dry-run that shows the actual rendered script."

3. **`ScriptExecutionRequest.Files` has two incompatible contracts.** K8s handlers emit `content/foo.yaml` (nested), SSH strategy rejects any `/` or `\` in file names. The K8s-on-SSH combination is silently broken today.

4. **Capabilities are implicit, not validated.** Whether a transport supports `Python`, `nested files`, `sudo`, `kubectl`, etc. is hardcoded in scattered `switch` blocks. There is no single place that says "this action cannot run on this target, fail at prepare phase."

The fix is a **generic capability-based architecture** where:

- Handlers return **semantic intents** (`RunScriptIntent`, `KubernetesApplyIntent`, ...), not rendered scripts
- Each transport declares a **`ITransportCapabilities`** object with what it supports
- A per-transport **`IIntentRenderer`** translates intent → `ScriptExecutionRequest`
- A **`IDeploymentPlanner`** service owns target filtering + intent description + capability validation, shared by preview and execute
- A **`IRuntimeBundleProvider`** injects bash helpers (`set_squidvariable`, `new_squidartifact`, ...) aligned with the existing `ServiceMessageParser`

---

## 2. Target Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                         DeploymentPlanner                          │
│  (generic: shared by Preview + Executor, PlanMode enum switches)   │
│                                                                    │
│  PlanAsync(DeploymentPlanRequest) → DeploymentPlan                 │
│   ├─ Load deployment + process snapshot                            │
│   ├─ Resolve variables (both modes)                                │
│   ├─ Find candidate targets                                        │
│   ├─ Apply machine selection (specific/excluded)                   │
│   ├─ Filter by health (skipped in Preview mode)                    │
│   ├─ Filter steps/actions (env, channel, role, disabled)           │
│   ├─ Match targets to steps                                        │
│   ├─ Call IActionHandler.DescribeIntentAsync → ExecutionIntent     │
│   └─ ICapabilityValidator.Validate(intent, caps) → violations      │
└────────────────────────────────────────────────────────────────────┘
            │                                          │
            ▼                                          ▼
   Preview: plan → map → response          Execute: plan → render → execute
                                                       │
                                                       ▼
┌────────────────────────────────────────────────────────────────────┐
│                         IIntentRenderer                            │
│  (per-transport: SSH / K8s API / K8s Agent / OpenClaw / future)    │
│                                                                    │
│  RenderAsync(ExecutionIntent, IntentRenderContext)                 │
│                 → ScriptExecutionRequest                           │
│                                                                    │
│  Uses:                                                             │
│   ├─ IRuntimeBundleProvider (bash/pwsh helper injection)           │
│   ├─ IPackageStagingPlanner (full upload / cache hit / remote DL)  │
│   └─ Transport-specific context building (kubectl ctx, env exports)│
└────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌────────────────────────────────────────────────────────────────────┐
│                       IExecutionStrategy                           │
│  (thin per-transport execution — no rendering, no capability check)│
│                                                                    │
│  ExecuteScriptAsync(ScriptExecutionRequest) → ScriptExecutionResult│
└────────────────────────────────────────────────────────────────────┘
```

### Cross-cutting services (transport-agnostic)

| Service | Responsibility |
|---|---|
| `ITransportCapabilities` | Declares what a transport supports (syntaxes, nested files, staging modes, features) |
| `IDeploymentPlanner` | Produces `DeploymentPlan` from a request; shared by Preview + Execute |
| `ICapabilityValidator` | Returns a list of violations for an (intent, capabilities) pair (non-throwing) |
| `IPackageStagingPlanner` | Decides how to get a package onto a target (upload / cache / remote download / delta) |
| `IRuntimeBundleProvider` | Produces embedded helper script (`squid-runtime.sh`, `squid-runtime.ps1`) |
| `IServiceMessageParser` | Existing — parses `##squid[...]` envelopes with base64 attribute decoding |

---

## 3. Key Design Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | `DeploymentPlanRequest.IncludeRenderedRequests = false` by default | Preview stays fast and side-effect free; renderer can invoke feed downloads for K8s packages which is not acceptable in preview |
| D2 | Variables resolve in **both** Preview and Execute | Without variables, `DescribeIntentAsync` cannot substitute `#{Namespace}` etc.; resolution itself is side-effect-free |
| D3 | `ICapabilityValidator.Validate` returns violations, does not throw | Preview must show *all* violations at once; executor wraps with `throw new DeploymentPlanValidationException(plan.BlockingReasons)` |
| D4 | Old `DeploymentService.Preview.cs` is deleted in Phase 6c | Parity tests guard the migration; one source of truth is the goal |
| D5 | Old `Dictionary<string, byte[]> Files` is kept `[Obsolete]` through Phase 10 | Big-bang on file contract is too risky; parallel existence lets each handler migrate independently |
| D6 | `IScriptContextWrapper` is kept `[Obsolete]` through Phase 9 | Phase 9's `IIntentRenderer` absorbs its responsibilities; deleting earlier breaks K8s API context wrapping |
| D7 | ActionType strings become `ActionTypes.*` constants in Phase 3 | Prevents magic-string drift between handler registry and `ITransportCapabilities.SupportedActionTypes` |
| D8 | Phase 9 splits handler migration into one branch per handler | Reduces review burden, allows staged rollback |

---

## 4. Phase Plan

| Phase | Goal | Breaking? | Test Coverage Target |
|---|---|---|---|
| **1** | `DeploymentFile` contract stabilisation | No (parallel) | 7 unit / 3 integration / 1 E2E |
| **2** | `ITransportCapabilities` collection | No | 8 unit |
| **3** | `ActionTypes` constants + `IServiceMessageParser` abstraction | No | 7 unit / 2 integration |
| **4** | `ExecutionIntent` semantic model (types only) | No | 5 unit |
| **5** | `IIntentRenderer` + adapter (handlers unchanged) | No | 20 unit / 1 integration |
| **5.5** | `ICapabilityValidator` (non-throwing) | No | 7 unit |
| **6** | `IDeploymentPlanner` unifying Preview + Execute | Yes (deletes old preview) | 23 unit / 9 integration / 6 E2E |
| **7** | `IPackageStagingPlanner` abstraction | No | 4 unit / 2 integration / 3 E2E |
| **8** | `IRuntimeBundleProvider` + bash/pwsh helpers | No | 5 unit / 5 integration / 4 E2E |
| **9** | Handler migration to `DescribeIntentAsync` | Yes (iface change) | 12 unit / 2 integration |
| **10** | Cleanup, `[Obsolete]` removal, contract lock | Yes (removes old API) | 3 lock tests |

Total estimated new tests: **~140**.

---

## 5. Phase Details

### Phase 1 — `DeploymentFile` contract

**Goal**: Replace `Dictionary<string, byte[]>` with a typed `DeploymentFile` collection that explicitly supports nested relative paths and file metadata (kind, executable flag).

**New files** (under `Script/Files/`):
- `DeploymentFile.cs` — record with `RelativePath`, `Content`, `Kind`, `IsExecutable`, static factories `Script`, `Asset`, `Package`, `Bootstrap`, `RuntimeBundle`, instance `EnsureValid`
- `DeploymentFileKind.cs` — enum: Script, Package, Asset, Bootstrap, RuntimeBundle
- `DeploymentFileCollection.cs` — thin wrapper implementing `IReadOnlyList<DeploymentFile>`, enforces uniqueness and validation

**Changed files**:
- `ScriptExecutionRequest.cs` — add `IReadOnlyList<DeploymentFile> DeploymentFiles`, keep old `Files` as `[Obsolete]`
- `SshExecutionStrategy.cs` — relax filename validation: reject only `..` and rooted paths; support `mkdir -p` for nested
- `SshFileTransfer.cs` — add `EnsureDirectoryTreeAsync(basePath, relativeTree, ct)`
- `KubernetesDeployYamlActionHandler.cs` — emit `DeploymentFile` records with `content/…` nesting

**Tests**:
- Unit (`tests/Squid.UnitTests/Services/DeploymentExecution/Script/Files/`):
  - `DeploymentFileTests` — validation, path traversal, rooted path, factories (7)
  - `DeploymentFileCollectionTests` — duplicate path, nested detection (3)
  - `SshFileValidationTests` — parametric path accept/reject (4)
- Integration:
  - `SshNestedFileUploadTests` — real SSH to localhost, verifies nested tree creation, path traversal rejection (3)
- E2E:
  - Update `KubernetesDeployYamlE2ETests` to verify package-based deployment uses `content/` prefix (1)

**Done criteria**:
- `dotnet build` zero warnings except intentional `[Obsolete]`
- All existing tests green
- New unit test count ≥ 11
- SSH can upload nested file trees
- K8s `DeployYaml` from package works end-to-end

---

### Phase 2 — `ITransportCapabilities`

**Goal**: Collect scattered capability flags into a single static declaration per transport.

**New files** (under `Transport/`):
- `ITransportCapabilities.cs` — interface with `SupportedSyntaxes`, `SupportsNestedFiles`, `SupportsExecutableFlag`, `MaxFileSizeBytes`, `PackageStagingModes`, `ExecutionLocation`, `ExecutionBackend`, `SupportsOutputVariables`, `SupportsArtifactCollection`, `SupportsSudo`, `SupportsIsolationMutex`, `RequiresContextPreparationForPackagedPayload`, `SupportedActionTypes`, `OptionalFeatures`
- `TransportCapabilities.cs` — immutable record implementing the interface with `init;` setters
- `PackageStagingMode.cs` — `[Flags]` enum: None, UploadOnly, CacheAware, RemoteDownload, Delta

**Changed files**:
- `IDeploymentTransport.cs` — add `ITransportCapabilities Capabilities`; mark `ExecutionLocation`, `ExecutionBackend`, `RequiresContextPreparationForPackagedPayload`, `ScriptWrapper` as `[Obsolete]` forwarding to `Capabilities`
- `DeploymentTransport.cs` — accept `ITransportCapabilities` in ctor
- `SshTransport.cs`, `KubernetesApiTransport.cs`, `KubernetesAgentTransport.cs`, `OpenClawTransport.cs` — each declares `static readonly ITransportCapabilities Capability` and passes it to base ctor

**Tests**:
- Unit: `TransportCapabilitiesTests` — per-transport assertion of supported syntaxes, nested files, package modes, action types (8)

**Done criteria**:
- Every transport exposes a non-null `Capabilities`
- Obsolete forwarders still work

---

### Phase 3 — `ActionTypes` constants + `IServiceMessageParser` abstraction

**Goal**: Eliminate magic-string drift, extract the parser behind an interface, and widen the parser to emit structured messages.

**New files**:
- `Handlers/ActionTypes.cs` — public const strings for every current action type + `IReadOnlySet<string> All`
- `Script/ServiceMessages/IServiceMessageParser.cs`
- `Script/ServiceMessages/ParsedServiceMessage.cs` — record with `Kind` and `Attributes`
- `Script/ServiceMessages/ServiceMessageKind.cs` — enum: SetVariable, CreateArtifact, StepFailed, StdWarning
- `Script/ServiceMessages/ServiceMessageParser.cs` — moved & extended existing parser

**Changed files**:
- Every `IActionHandler` implementation — replace literal `"Squid.Script"` with `ActionTypes.RunScript`
- `Script/ServiceMessageParser.cs` → delete (moved)

**Tests**:
- Unit: `ServiceMessageParserTests` — setVariable (base64 + legacy, both prefixes), createArtifact, stepFailed, mixed lines, sensitive flag (7)
- Integration: reflection-based drift test — every `IActionHandler.ActionType` must be in `ActionTypes.All` (2)

**Done criteria**:
- `git grep '"Squid\.' src/ | grep -v Tests` returns zero handler matches

---

### Phase 4 — `ExecutionIntent` semantic model

**Goal**: Introduce the intent record hierarchy. No existing code calls them yet.

**New files** (under `Intents/`):
- `ExecutionIntent.cs` — abstract record with `Name`, `Assets`, `RequiredCapabilities`, `Packages`, `Timeout`, `StepName`, `ActionName`
- `RunScriptIntent.cs` — `ScriptBody`, `Syntax`, `InjectRuntimeBundle`
- `DeployPackageIntent.cs` — `Package`, `ExtractTo`, `PreDeployScript`, `PostDeployScript`
- `KubernetesApplyIntent.cs` — `YamlFiles`, `Namespace`, `ServerSideApply`
- `HelmUpgradeIntent.cs` — `ReleaseName`, `ChartReference`, `Namespace`, `ValuesFile`, `InlineValues`
- `HealthCheckIntent.cs` — `CustomScript`
- `IntentCapabilityKeys.cs` — constant strings for feature flags (`bash`, `kubectl`, `helm`, `docker`, etc.)

**Tests**:
- Unit: `ExecutionIntentTests` — required fields, immutability, `with` expression (5)

**Done criteria**: New files only, no behavioural change.

---

### Phase 5 — `IIntentRenderer` with legacy adapter

**Goal**: Insert the renderer layer into the pipeline *without* changing any handler. Use a `LegacyIntentAdapter` to convert the existing `ActionExecutionResult` into an intent, so Phase 5 can ship independently.

**New files**:
- `Rendering/IIntentRenderer.cs`
- `Rendering/IntentRenderContext.cs`
- `Rendering/IntentRendererRegistry.cs`
- `Rendering/Exceptions/UnsupportedIntentException.cs`
- `Rendering/Exceptions/IntentRenderingException.cs`
- `Rendering/Adapters/LegacyIntentAdapter.cs` — converts `ActionExecutionResult` + `DeploymentActionDto` into the matching intent record
- `Targets/Ssh/Rendering/SshIntentRenderer.cs`
- `Targets/Kubernetes/Rendering/KubernetesApiIntentRenderer.cs`
- `Targets/Kubernetes/Rendering/KubernetesAgentIntentRenderer.cs`
- `Targets/OpenClaw/Rendering/OpenClawIntentRenderer.cs`

**Changed files**:
- `6_ExecuteStepsPhase.Prepare.cs` — after `handler.PrepareAsync`, call `LegacyIntentAdapter.FromLegacyResult` and then `IntentRendererRegistry.Resolve(...).RenderAsync`

**Tests**:
- Unit: `SshIntentRendererTests`, `KubernetesApiIntentRendererTests`, `KubernetesAgentIntentRendererTests`, `LegacyIntentAdapterTests` (20+)
- Integration: `IntentRendererRegistryTests` for DI resolution (1)

**Done criteria**: All existing E2E tests remain green (adapter is behaviour-preserving).

---

### Phase 5.5 — `ICapabilityValidator` (non-throwing)

**Goal**: A tiny self-contained service that returns `List<CapabilityViolation>` for an `(intent, capabilities)` pair. No pipeline integration yet.

**New files** (under `Validation/`):
- `ICapabilityValidator.cs`
- `CapabilityValidator.cs`
- `CapabilityViolation.cs` — record with `Code`, `Message`, optional IDs
- `ViolationCodes.cs` — constants: `UNSUPPORTED_ACTION_TYPE`, `UNSUPPORTED_SYNTAX`, `NESTED_FILES`, `MISSING_FEATURE`, `PACKAGE_STAGING`

**Tests**:
- Unit: `CapabilityValidatorTests` covering each violation code + multi-violation accumulation (7)

**Done criteria**: Validator is DI-registered but unused by pipeline (will be consumed in Phase 6).

---

### Phase 6 — `IDeploymentPlanner` unifying Preview + Execute

**This is the structural centrepiece of the refactor.**

**New files** (under `Planning/`):
- `IDeploymentPlanner.cs`
- `DeploymentPlanner.cs` — flat pipeline following the project's refactoring pattern
- `DeploymentPlanRequest.cs`
- `DeploymentPlan.cs`
- `PlanMode.cs` — enum: Preview, Execute
- `PlannedStep.cs`
- `PlannedAction.cs`
- `PlannedTarget.cs`
- `PlannedTargetDispatch.cs`
- `PlanBlockingReason.cs`
- `StepSkipReason.cs`
- `CapabilityValidationResult.cs`
- `PlanContext.cs` — internal, shared state during `PlanAsync` (field-context pattern)
- `Exceptions/DeploymentPlanValidationException.cs`

**Deleted files**:
- `src/Squid.Core/Services/Deployments/Deployments/DeploymentService.Preview.cs` (after parity tests pass)

**Changed files**:
- `PreviewDeploymentRequestHandler.cs` — thin wrapper calling `IDeploymentPlanner.PlanAsync` with `PlanMode.Preview`
- `DeploymentPreviewResult.cs` — mapper adjusted to new `DeploymentPlan` shape
- `4_PrepareDeploymentPhase.cs` — after loading, call `_planner.PlanAsync(PlanMode.Execute)` and store result in `_ctx.Plan`; throw if `!CanProceed`
- `5_PrepareTargetsPhase.cs` — populate target contexts from `ctx.Plan` instead of re-running filtering
- `6_ExecuteStepsPhase.Execute.cs` — iterate over `ctx.Plan.Steps` and dispatch each `PlannedTargetDispatch` via renderer
- `DeploymentTaskContext.cs` — add `DeploymentPlan Plan`

**Tests**:
- Unit (`DeploymentPlannerTests`, 23+):
  - Load path, variable resolution, target find, selection filters, health filter gated by mode
  - Step disabled / action disabled / env mismatch / channel mismatch / skip action IDs
  - Role matching, intent describe per target, describe-throws handling per mode
  - Validation accumulation, blocking reason aggregation
- Integration (`DeploymentPlannerIntegrationTests`, 9):
  - Real DB, cross-mode parity, multi-transport plans
- **Parity tests** (`PreviewExecutionParityTests`, parameterised) — for each representative scenario, `PlanMode.Preview` and `PlanMode.Execute` produce structurally identical plans (ignoring `Mode` and health-filter)
- E2E (`PreviewPlanE2ETests`, 6): endpoint returns plan with intents, capability violations reflected in preview, executor uses plan from planner

**Done criteria**:
- `PreviewExecutionParityTests` all green for ≥ 5 scenarios
- Old `DeploymentService.Preview.cs` deleted
- Executor phase 4 + 5 + 6 use `ctx.Plan` exclusively

---

### Phase 7 — `IPackageStagingPlanner` ✅

**Goal**: Extract SSH's ad-hoc MD5-cache decision into a pluggable strategy service that future transports can reuse and future staging modes (delta, remote download) can extend.

**New files** (under `Packages/Staging/`):
- `IPackageStagingPlanner.cs` ✓
- `PackageStagingPlanner.cs` — dispatches to first matching `IPackageStagingHandler` by priority ✓
- `PackageStagingPlan.cs` — record with strategy + materialised data ✓
- `PackageStagingStrategy.cs` — enum: FullUpload, CacheHit, RemoteDownload, Delta ✓
- `PackageRequirement.cs` ✓
- `PackageStagingContext.cs` — abstract record; transports supply derived subtypes ✓
- `IPackageStagingHandler.cs` — `Priority`, `CanHandle`, `TryPlanAsync` ✓
- `Exceptions/PackageStagingFailedException.cs` ✓
- `Handlers/CacheHitStagingHandler.cs` (Priority 100) ✓
- `Handlers/FullUploadStagingHandler.cs` (Priority 10) ✓
- `Handlers/RemoteDownloadStagingHandler.cs` (stub, Priority 80) ✓
- `Handlers/DeltaStagingHandler.cs` (stub, Priority 70) ✓

**Changed files**:
- `Targets/Ssh/Packages/SshPackageStagingContext.cs` — derived record carrying `ISshConnectionScope` ✓
- `Targets/Ssh/Connectivity/ICachedPackageLookup.cs` + `SshCachedPackageLookup.cs` — read-side primitive ✓
- `Targets/Ssh/Connectivity/IFullPackageUploader.cs` + `SshFullPackageUploader.cs` — write-side primitive ✓
- `Targets/Ssh/Connectivity/SshPackageTransfer.cs` — shrunk to the post-staging `ExtractPackage` helper ✓
- `Targets/Ssh/Transport/SshExecutionStrategy.cs` — ctor-injects `IPackageStagingPlanner`, new `StageAndExtractPackagesAsync` builds `SshPackageStagingContext` per package and lets the planner decide ✓

> Note: the planner is consumed by `SshExecutionStrategy` today. When Phase 9 flips handlers to `DescribeIntentAsync`, `SshIntentRenderer` will pick up the same call for `DeployPackageIntent` without touching the planner or its handlers.

**Tests**:
- Unit: `PackageStagingPlannerTests` — priority ordering, fall-through on `CanHandle` false, fall-through on null plan, guard clauses, exhaustion throws, out-of-order registration (9) ✓
- Integration: `SshCacheHitIntegrationTests` — real SSH, cache hit on second upload (2) — deferred
- E2E: `SshPackageStagingE2ETests` — first-time full upload, second-time cache hit, hash change forces re-upload (3) — deferred

---

### Phase 8 — `IRuntimeBundleProvider` + bash/pwsh helpers

**Goal**: Embed a helper script that gives user scripts `set_squidvariable`, `get_squidvariable`, `new_squidartifact`. Close the loop with the existing `ServiceMessageParser`.

**New files** (under `Runtime/`):
- `IRuntimeBundleProvider.cs`
- `RuntimeBundleKind.cs` — enum: Bash, PowerShell, Python
- `Bundles/BashRuntimeBundle.cs`
- `Bundles/PowerShellRuntimeBundle.cs`
- `Bundles/Resources/squid-runtime.sh` (embedded)
- `Bundles/Resources/squid-runtime.ps1` (embedded)

**Changed files**:
- `SshIntentRenderer.cs` — uses `IRuntimeBundleProvider.GetAsync(Bash, ctx, ct)` when `RunScriptIntent.InjectRuntimeBundle = true`
- `SshBootstrapper.cs` — mark `WrapBashScript`, `WrapWithVariableExports` `[Obsolete]` forwarding to the bundle provider
- `Script/ServiceMessages/ServiceMessageParser.cs` — add `createArtifact` + `stepFailed` kind support (if not done in Phase 3)

**Tests**:
- Unit: `BashRuntimeBundleTests` — placeholder replacement, sensitive var skip, sanitization, quote escaping (5)
- Integration (real bash): `BashBundleShellIntegrationTests` — `set_squidvariable` round-trip, `new_squidartifact`, `fail_step` exit code (4)
- E2E: `SshRuntimeBundleE2ETests` — `set_squidvariable` captured in output variables, `new_squidartifact` registered, sensitive not in exports (3)

---

### Phase 9 — Handler migration to `DescribeIntentAsync`

**Goal**: Flip every handler from producing `ActionExecutionResult` to producing `ExecutionIntent`. Remove `LegacyIntentAdapter`. Delete `IScriptContextWrapper`.

**Migration order** (one branch per handler):
1. `RunScriptActionHandler` → `RunScriptIntent`
2. `KubernetesDeployYamlActionHandler` → `KubernetesApplyIntent`
3. `HelmUpgradeActionHandler` → `HelmUpgradeIntent`
4. `KubernetesDeployConfigMapActionHandler` / `Secret` / `Service` / `Ingress` / `Kustomize` / `DeployContainers` → `KubernetesApplyIntent`
5. `HealthCheckActionHandler` → `HealthCheckIntent`
6. `ManualInterventionActionHandler` → new `ManualInterventionIntent` (no-op for renderers)
7. OpenClaw handlers → new `OpenClawInvokeIntent` variants

**Changed files**:
- `IActionHandler.cs` — `DescribeIntentAsync` is the primary method; `PrepareAsync` becomes `[Obsolete]` default throwing
- Each migrated handler
- Delete:
  - `LegacyIntentAdapter.cs`
  - `IScriptContextWrapper.cs` + all implementations
  - `IDeploymentTransport.ScriptWrapper`

**Tests**:
- Unit: per-handler `DescribeIntentAsync` tests covering all branches (12+)
- Integration: reflection drift test — no handler still overrides `PrepareAsync` (2)

---

### Phase 10 — Cleanup + contract lock

**Goal**: Remove every `[Obsolete]` marker introduced by this refactor and lock the new public surface with reflection tests.

**Removed**:
- `ScriptExecutionRequest.Files` (`Dictionary<string, byte[]>`)
- `IDeploymentTransport.ExecutionLocation`, `ExecutionBackend`, `RequiresContextPreparationForPackagedPayload`, `ScriptWrapper`
- `IActionHandler.PrepareAsync`
- `SshBootstrapper.WrapBashScript`, `WrapWithVariableExports`

**New tests** (`ContractLockTests.cs`):
- `IDeploymentTransport` public surface matches expected member list
- `IIntentRenderer` public surface matches
- `ExecutionIntent` concrete subtypes enumerated match a known set

**Documentation**:
- `CLAUDE.md` extension-pattern section updated to say: "To add a new target type, implement `ITransportCapabilities`, `IEndpointVariableContributor`, `IIntentRenderer`, `IExecutionStrategy`, `IHealthCheckStrategy` and wire them via a `DeploymentTransport` subclass."

---

## 6. Testing Strategy

Every phase follows the project's **test-first Red-Green-Refactor** rule. All three tiers are mandatory for any behavioural change:

| Tier | Location | Covers |
|---|---|---|
| **Unit** | `tests/Squid.UnitTests/Services/DeploymentExecution/` | Pure logic, no DB, no network. Every branch in validators, planners, renderers, file validation |
| **Integration** | `tests/Squid.IntegrationTests/` | Real Postgres, real bash shell, real SSH to localhost. DI graph resolution, DB persistence, file tree creation |
| **E2E** | `tests/Squid.E2ETests/` | Full pipeline through `DeploymentTaskExecutor` or real HTTP endpoint. Real Kind cluster, real TentacleStub, real SSH |

### Parity test (the most important single test of this refactor)

`tests/Squid.UnitTests/Services/DeploymentExecution/Planning/PreviewExecutionParityTests.cs`:

```csharp
[Theory]
[InlineData("simple-bash-ssh")]
[InlineData("k8s-yaml-api")]
[InlineData("k8s-yaml-agent")]
[InlineData("helm-upgrade")]
[InlineData("multi-step-multi-target")]
public async Task Preview_And_Execute_Plan_Are_Identical_For_Scenario(string scenario)
```

If this test ever goes red, preview has drifted from execute. CI must block merges on it.

---

## 7. Risk Matrix

| Risk | Impact | Mitigation |
|---|---|---|
| Phase 5 adapter introduces behaviour change | Existing E2E fail | Adapter phase adds code only, no deletions; all E2E must pass before merge |
| Bash bundle fails on hosts without `openssl` | SSH deploy fails | Runtime bundle falls back to `base64 -w 0`; integration test on Alpine runner |
| Nested SFTP mkdir fails due to permissions | SSH deploy fails | Integration test exercises `$HOME/.squid/.../content/nested/`; error messages include full remote path |
| `IScriptContextWrapper` deletion misses a K8s branch | K8s API deploy breaks | Phase 9 requires all K8s E2E green **before** deletion; renderer tests land in separate PR first |
| ActionType constant drift | Handler resolution fails | Phase 3 integration test uses reflection to assert all handlers' `ActionType` are in `ActionTypes.All` |
| Pre-flight false positives block legitimate deployments | Deploys fail at prepare | Phase 6 E2E covers all current happy paths; grey-launch validator as log-only for one week if concerned |
| Planner returns different plan than old preview on edge case | User sees regression | `PreviewExecutionParityTests` and a dedicated `LegacyPreviewCompatTests` that constructs plans via both old + new code paths during Phase 6 |

---

## 8. Rollout Order

```
Phase 1  →  Phase 2  →  Phase 3  →  Phase 4
   (independent; can review in parallel; no behaviour change)
                     ↓
            Phase 5 (first behaviour-touching change, E2E-guarded)
                     ↓
            Phase 5.5
                     ↓
            Phase 6 (preview/execute unification — the centrepiece)
                     ↓
            Phase 7  →  Phase 8  (SSH deepening; can parallelise)
                     ↓
            Phase 9 (handler migration; one branch per handler)
                     ↓
            Phase 10 (cleanup + lock)
```

## 9. Definition of Done (per phase)

1. `dotnet build` with zero errors and zero warnings other than explicitly-added `[Obsolete]`
2. `dotnet test` green for Unit + Integration + E2E
3. New tests count ≥ the number stated in the phase's test section
4. `git grep` confirms no dangling magic strings or obsolete interfaces referenced outside legacy adapters
5. Every method ≤ 25 lines, ≤ 5 parameters, method parameters on one line, `.ConfigureAwait(false)` on same line as `)`
6. CLAUDE.md updated if the phase changes the extension pattern

## 10. Branch Naming

```
refactor/phase-1-deployment-file-contract
refactor/phase-2-transport-capabilities
refactor/phase-3-action-types-constants
refactor/phase-4-execution-intents
refactor/phase-5-intent-renderer-adapter
refactor/phase-5.5-capability-validator
refactor/phase-6-deployment-planner
refactor/phase-7-package-staging-planner
refactor/phase-8-runtime-bundle
refactor/phase-9-handler-migration-runscript
refactor/phase-9-handler-migration-k8s-yaml
refactor/phase-9-handler-migration-helm
refactor/phase-9-handler-migration-remaining
refactor/phase-10-cleanup-contract-lock
```
