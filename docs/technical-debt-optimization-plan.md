# Technical Debt Optimization Plan

Post Phase-10 refactor residual debt. Three workstreams, ordered by dependency and risk.

---

## Current State Summary

The Phase 5 bridge is still load-bearing: every action execution calls **both** `PrepareAsync` (legacy) and `DescribeIntentAsync` (new). The legacy result becomes `IntentRenderContext.LegacyRequest`, which renderers either return unchanged (fallback) or cherry-pick data from (`legacy?.Files`, `legacy?.PackageReferences`).

### FallbackToLegacy Coverage Map

| Intent Type | K8s API | K8s Agent | SSH | OpenClaw | Server | Notes |
|---|---|---|---|---|---|---|
| `RunScriptIntent` | **Native** | **Native** | **Native** | Fallback | Fallback | Core path, ~80% of deployments |
| `KubernetesApplyIntent` | **Native** | **Native** | N/A | N/A | N/A | Second most common |
| `OpenClawInvokeIntent` | N/A | N/A | N/A | **Native** | N/A | |
| `HelmUpgradeIntent` | Fallback | Fallback | N/A | N/A | N/A | Needs native render |
| `KubernetesKustomizeIntent` | Fallback | Fallback | N/A | N/A | N/A | Needs native render |
| `HealthCheckIntent` | **StepLevel** | **StepLevel** | **StepLevel** | **StepLevel** | N/A | Never reaches renderer |
| `ManualInterventionIntent` | **StepLevel** | **StepLevel** | **StepLevel** | **StepLevel** | N/A | Never reaches renderer |
| `DeployPackageIntent` | Fallback | Fallback | Fallback | N/A | Fallback | No handler produces it yet |

**Key insight**: `HealthCheckIntent` and `ManualInterventionIntent` are `ExecutionScope.StepLevel` — they dispatch via `ExecuteStepLevelAsync` and **never reach a renderer**. Their `FallbackToLegacy` path is dead code.

---

## Workstream A: Complete Native Rendering (eliminate FallbackToLegacy)

### Dependency Analysis

Renderers that natively render already read from `legacy?.Files` and `legacy?.PackageReferences`:
- `KubernetesApiIntentRenderer.RenderRunScript` → `legacy?.Files`, `legacy?.PackageReferences`
- `KubernetesAgentIntentRenderer.RenderRunScript` → `legacy?.Files`, `legacy?.PackageReferences`
- `SshIntentRenderer.RenderRunScript` → `legacy?.Files`, `legacy?.PackageReferences`

This means **even native render paths depend on `LegacyRequest`**. Before we can remove it, these data sources must move onto `ExecutionIntent` or `IntentRenderContext`.

### Phase A1: Promote Files + Packages onto ExecutionIntent

**Goal**: Intent carries all data renderers need — no `legacy?.` null-coalescing.

**Changes**:
- `ExecutionIntent.Assets` (already exists as `IReadOnlyList<DeploymentFile>`) replaces `legacy?.Files`
- `ExecutionIntent.Packages` (already exists as `IReadOnlyList<IntentPackageReference>`) replaces `legacy?.PackageReferences`
- Each handler's `DescribeIntentAsync` populates these fields (currently most leave them empty)

**Migration per handler**:

| Handler | Currently populates Assets? | Currently populates Packages? | Work needed |
|---|---|---|---|
| `RunScriptActionHandler` | No | No | Populate from `ActionProperties` if user attaches files |
| `KubernetesDeployYamlActionHandler` | Yes (YamlFiles on intent) | No | Add package refs if feed-sourced |
| `KubernetesYamlActionHandler` | Yes (YamlFiles) | No | Add container image package refs |
| `HelmUpgradeActionHandler` | No | No | Populate `ValuesFiles` as Assets, chart as Package |
| `KubernetesKustomizeActionHandler` | No | No | Minimal — no files currently |
| K8s resource handlers (ConfigMap/Secret/Service/Ingress) | Yes (YamlFiles) | No | Minimal |
| OpenClaw handlers | No | No | No files needed |

**Tests**: Per-handler unit tests asserting `Assets` and `Packages` are populated. Parity test: intent Assets == legacy Files for same scenario.

**Risk**: Low. Additive — handlers populate more fields on intent. No behavioral change.

### Phase A2: Native render HelmUpgradeIntent

**Goal**: K8s API + K8s Agent renderers natively render `HelmUpgradeIntent` → `ScriptExecutionRequest`.

**What the renderer must produce** (currently in `HelmUpgradeActionHandler.PrepareAsync`):
- Shell script: `helm upgrade --install {releaseName} {chartRef} --namespace {ns} [--values ...] [--wait] [--timeout ...]`
- Files: `rawYamlValues.yaml`, `values-*.yaml` (from intent's `ValuesFiles` / `InlineValues`)
- kubectl context preamble (K8s API only)

**Changes**:
- `KubernetesApiIntentRenderer`: add `RenderHelmUpgrade(HelmUpgradeIntent, context)` method
- `KubernetesAgentIntentRenderer`: add `RenderHelmUpgrade(HelmUpgradeIntent, context)` — simpler (no context preamble)
- Switch expression gains `HelmUpgradeIntent h => RenderHelmUpgrade(h, context)`

**Tests**: Unit tests per renderer. E2E: `HelmUpgradeE2ETests` must pass with native rendering.

**Risk**: Medium. Helm command construction has many branches (chart source, values, flags). Must match legacy output exactly.

### Phase A3: Native render KubernetesKustomizeIntent

**Goal**: K8s API + K8s Agent renderers natively render `KubernetesKustomizeIntent`.

**What the renderer must produce**:
- Shell script: `kustomize build {overlayPath} | kubectl apply -f -` (with optional `--server-side`, `--field-manager`, etc.)
- No files (kustomize reads from the overlay directory)
- kubectl context preamble (K8s API only)

**Changes**: Same pattern as A2. Simpler because kustomize has fewer branches.

**Tests**: Unit tests per renderer. E2E coverage for kustomize scenarios.

**Risk**: Low. Simpler than Helm.

### Phase A4: Remove FallbackToLegacy + PassThroughIntentRendererBase

**Goal**: Delete all fallback paths. Every renderer either natively renders or throws `UnsupportedIntentException`.

**Preconditions**: A1 + A2 + A3 complete. All E2E green.

**Changes**:
- Delete `FallbackToLegacy` method from all 4 renderers
- Delete `PassThroughIntentRendererBase`
- `ServerIntentRenderer` becomes a thin class that natively renders `RunScriptIntent` only (returns script body + files from intent.Assets)
- Switch expressions become exhaustive — unknown intent types throw

**Tests**: ContractLock test updated. Renderer unit tests verify `UnsupportedIntentException` for unknown intents.

**Risk**: Medium. Must verify no intent type is missed. Reflection test can assert: for each (transport, intent) pair, either CanRender returns false or RenderAsync succeeds.

---

## Workstream B: Remove PrepareAsync dual-path

**Dependency**: Workstream A must be complete (renderers no longer read from `LegacyRequest`).

### Phase B1: Move variable expansion to intent pipeline

**Goal**: Variable expansion and structured config replacement happen on `ExecutionIntent` fields, not on `ActionExecutionResult`.

**Current pipeline** (in `6_ExecuteStepsPhase.Prepare.cs`):
```
PrepareAsync → ActionExecutionResult
  → VariableExpander.ExpandString(result.ScriptBody, vars)
  → StructuredConfigurationVariableReplacer.ReplaceIfEnabled(result, vars)
  → BuildScriptExecutionRequest(result)
  → LegacyRequest
```

**New pipeline**:
```
DescribeIntentAsync → ExecutionIntent
  → IntentVariableExpander.Expand(intent, vars)   // new: expands all string fields on intent
  → IntentRendererRegistry.Resolve(style, intent)
  → renderer.RenderAsync(intent, context)
  → ScriptExecutionRequest (final, no LegacyRequest)
```

**Changes**:
- New `IntentVariableExpander` — applies `VariableExpander.ExpandString` to relevant intent fields (`RunScriptIntent.ScriptBody`, `KubernetesApplyIntent.YamlFiles` values, `HelmUpgradeIntent.InlineValues`, etc.)
- New `IntentConfigReplacer` — applies structured config replacement to intent's embedded content
- Pipeline calls these between `DescribeIntentAsync` and `RenderAsync`

**Tests**: Unit tests for `IntentVariableExpander` covering each intent type. Parity test: expanded intent produces same script as expanded legacy result.

**Risk**: Medium. Must handle every intent type's expandable fields correctly.

### Phase B2: Remove PrepareAsync from pipeline

**Goal**: `6_ExecuteStepsPhase.Prepare.cs` no longer calls `handler.PrepareAsync()`.

**Preconditions**: B1 complete. All renderers natively render without `LegacyRequest`.

**Changes**:
- Delete `PrepareAsync` call from `PrepareStepActionsAsync`
- Delete `BuildScriptExecutionRequest` method (no longer needed)
- Delete `IntentRenderContext.LegacyRequest` property
- `PreparedAction` record simplifies: no longer carries `ActionExecutionResult`
- Pipeline becomes:
  ```
  DescribeIntentAsync → ExecutionIntent
    → IntentVariableExpander.Expand(intent, vars)
    → renderer.RenderAsync(intent, context)
    → strategy.ExecuteScriptAsync(request)
  ```

**Tests**: Full E2E suite must pass. Pipeline acceptance tests updated.

**Risk**: High. This is the point of no return for the legacy bridge. Must be absolutely confident all intent types are covered.

### Phase B3: Remove PrepareAsync from IActionHandler interface

**Goal**: Clean interface.

**Changes**:
- Remove `PrepareAsync` from `IActionHandler` interface
- Remove `PrepareAsync` from all 18 handler implementations
- Remove `ActionExecutionResult` class (or repurpose if needed elsewhere)
- ContractLock test updated to assert `IActionHandler` surface

**Tests**: Reflection test asserting no handler has a `PrepareAsync` method.

**Risk**: Low (mechanical deletion after B2 is stable).

---

## Workstream C: Migrate ScriptExecutionRequest.Files

**Dependency**: Workstream A (renderers populate Files from intent.Assets, not from legacy).

### Phase C1: Adopt DeploymentFileCollection as primary

**Goal**: All producers write to `DeploymentFileCollection` (typed, validated), not `Dictionary<string, byte[]>`.

**Current state**: `ScriptExecutionRequest` has both:
- `Files` (`Dictionary<string, byte[]>`) — legacy, used everywhere
- `DeploymentFiles` (`DeploymentFileCollection`) — new, derived on-demand from `Files`

**Changes**:
- All renderer `RenderAsync` methods produce `DeploymentFileCollection` directly
- `DeploymentFileCollection` becomes the primary field
- `Files` becomes a computed backward-compat getter (reverse direction)
- Execution strategies migrate to read `DeploymentFiles`

**Tests**: Strategy unit tests updated. E2E unchanged (behavioral parity).

**Risk**: Medium. Many consumers to update.

### Phase C2: Delete legacy Files dictionary

**Goal**: Single file representation.

**Preconditions**: C1 complete. All consumers use `DeploymentFiles`.

**Changes**:
- Remove `Files` property from `ScriptExecutionRequest`
- Remove `ToLegacyFiles` / `FromLegacyFiles` conversion methods
- ContractLock test updated

**Risk**: Low (mechanical after C1).

---

## Workstream D: Minor cleanups

### D1: Remove SpecialVariables.Kubernetes.Namespace from pipeline

**Location**: `6_ExecuteStepsPhase.Prepare.cs:169`

**Change**: Move namespace resolution into `IntentRenderContext` population or into the renderer itself. The pipeline should set a generic `TargetNamespace` field on the render context, sourced from `EndpointVariables` — not from a K8s-named special variable.

**Risk**: Low.

### D2: Remove dead FallbackToLegacy paths for StepLevel intents

**Location**: All renderers have `FallbackToLegacy` for `HealthCheckIntent` and `ManualInterventionIntent`, but these intents never reach renderers (they're dispatched via `ExecuteStepLevelAsync`).

**Change**: Can be done independently of Workstream A. Add explicit `CanRender` filtering or just leave — the code is unreachable but harmless.

**Risk**: None.

### D3: ServerIntentRenderer upgrade

**Location**: `Transport/ServerIntentRenderer.cs`

**Change**: Replace `PassThroughIntentRendererBase` inheritance with native `RunScriptIntent` rendering (the only intent Server transport handles). This aligns with Workstream A4 which deletes the base class.

**Risk**: Low.

---

## Execution Order and Dependencies

```
Phase A1 (Assets/Packages on intent)
  │
  ├──→ Phase A2 (native HelmUpgrade)
  │       │
  ├──→ Phase A3 (native Kustomize)
  │       │
  └──→ Phase A4 (delete FallbackToLegacy)  ←── requires A2 + A3
          │
          ├──→ Phase B1 (intent variable expansion)
          │       │
          │       └──→ Phase B2 (remove PrepareAsync from pipeline)
          │               │
          │               └──→ Phase B3 (remove PrepareAsync from interface)
          │
          └──→ Phase C1 (DeploymentFileCollection primary)
                  │
                  └──→ Phase C2 (delete Files dictionary)

Independent:
  Phase D1 (SpecialVariables.Kubernetes.Namespace)
  Phase D2 (dead StepLevel fallback paths)
  Phase D3 (ServerIntentRenderer upgrade)  ←── before or with A4
```

## Priority and Effort Estimates

| Phase | Priority | Effort | Risk | Blocking |
|---|---|---|---|---|
| **A1** | P0 | Medium (2-3 days) | Low | Blocks A2, A3, A4, C1 |
| **A2** | P1 | Medium (2-3 days) | Medium | Blocks A4 |
| **A3** | P1 | Small (1 day) | Low | Blocks A4 |
| **A4** | P1 | Small (1 day) | Medium | Blocks B1 |
| **B1** | P2 | Medium (2-3 days) | Medium | Blocks B2 |
| **B2** | P2 | Medium (2-3 days) | High | Blocks B3 |
| **B3** | P2 | Small (half day) | Low | Terminal |
| **C1** | P2 | Medium (2-3 days) | Medium | Blocks C2 |
| **C2** | P3 | Small (half day) | Low | Terminal |
| **D1** | P3 | Trivial | None | Independent |
| **D2** | P3 | Trivial | None | Independent |
| **D3** | P3 | Small (half day) | Low | Before/with A4 |

**Total estimated effort**: ~15-20 working days

**Critical path**: A1 → A2 → A4 → B1 → B2 → B3 (~12-15 days)

---

## Success Criteria

After all workstreams complete:

1. **Zero dual-path execution** — pipeline calls only `DescribeIntentAsync`, never `PrepareAsync`
2. **Zero FallbackToLegacy** — every renderer natively renders or explicitly rejects
3. **Zero LegacyRequest** — `IntentRenderContext` has no backward-compat bridge
4. **Single file representation** — `DeploymentFileCollection` only, no `Dictionary<string, byte[]>`
5. **Pipeline is intent-only** — the flow is:
   ```
   DescribeIntentAsync → expand variables → render → execute
   ```
6. **ContractLock tests** updated to assert:
   - `IActionHandler` has no `PrepareAsync`
   - `IntentRenderContext` has no `LegacyRequest`
   - `ScriptExecutionRequest` has no `Files` dictionary

7. **All 4064+ unit tests green, all E2E green, zero new warnings**
