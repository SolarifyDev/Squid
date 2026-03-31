# Squid Architecture Reference

## Architecture Comparison: Octopus → Squid

### Octopus Core Patterns (Reference)

| Pattern | Octopus Implementation | Squid Equivalent | Notes |
|---------|----------------------|------------------|-------|
| `IContributeVariables` | Interface on `Endpoint` / `Account` entities, each calls `ContributeVariables(VariableCollection)` in `MachineVariableCollector` | `IEndpointVariableContributor` — external contributors resolved by `CommunicationStyle` | Squid is more flexible (no entity coupling) |
| `IScriptWrapper` chain | Multiple wrappers compose sequentially (kubectl, az login, etc.) | `IScriptContextWrapper` — single wrapper per `CommunicationStyle` via `FirstOrDefault` | Sufficient for 1:1 style-to-wrapper mapping |
| Endpoint Polymorphism | `Endpoint` abstract base + `EndpointConverter` (maps `CommunicationStyle` enum → C# subclass) | JSON string + `ParseCommunicationStyle()` + per-contributor deserialization | Squid is more flexible (no class hierarchy) |
| Account Polymorphism | `Account` abstract base + `AccountConverter` (maps `AccountType` enum → C# subclass) | Single flat `DeploymentAccount` entity with `AccountType` enum | Squid is simpler, adequate |
| Convention Pipeline | 20+ `IInstallConvention` / `IRollbackConvention` in `ConventionProcessor` | Flat pipeline in `DeploymentTaskExecutor.ProcessAsync()` | Squid is simpler, sufficient for current scope |
| `SpecialVariables` | Centralized static class (1208 lines) with nested classes per category | Hardcoded strings in contributors/handlers | Future improvement — not blocking genericity |
| `IActionHandler` | Per-action-type handler in Calamari | `IActionHandler` + `ActionHandlerRegistry` | Equivalent pattern |

### Squid Current Architecture

| Layer | Status | Notes |
|-------|--------|-------|
| **Handler layer** (`IActionHandler`, `ActionHandlerRegistry`) | Generic | Target-agnostic dispatch by `ActionType` |
| **K8s Handlers** (RunScript, DeployYaml, Helm, Yaml) | Complete | Each handler returns `ActionExecutionResult` |
| **Pipeline layer** (`DeploymentTaskExecutor`) | Generic | Zero target-type-specific code in executor |
| **Variable contribution** (`IEndpointVariableContributor`) | Generic | Resolved once per deployment, cached in `_resolvedContributor` |
| **Script context wrapping** (`IScriptContextWrapper`) | Generic | Target types implement to wrap scripts (kubectl context, etc.) |
| **Calamari execution** (`DeploymentTaskExecutor.Calamari.cs`) | Generic | No target-type dependencies |
| **DI auto-registration** (`IScopedDependency`) | Autofac scan | All implementations auto-discovered via `ApplicationStartup.RegisterDependency` |

### Extension Pattern (Adding a New Target Type)

To add a new target type (e.g., SSH, Azure, Docker):

1. Implement `IEndpointVariableContributor` — parse account ID and contribute variables for the style. Use `EndpointVariableFactory.Make()` for variable creation and `EndpointVariableFactory.TryDeserialize<T>()` for endpoint JSON parsing.
2. Implement `IScriptContextWrapper` — wrap scripts with target-specific context (auth, namespace, etc.)
3. Implement `IExecutionStrategy` — execute scripts via the appropriate transport mechanism
4. Create a `*Transport.cs` — 5-line declarative wire-up using the `DeploymentTransport` base class:
   ```csharp
   public sealed class SshTransport(
       SshEndpointVariableContributor variables,
       SshScriptContextWrapper scriptWrapper,
       SshExecutionStrategy strategy)
       : DeploymentTransport(CommunicationStyle.Ssh, variables, scriptWrapper, strategy);
   ```
5. (Optional) Implement `IActionHandler` subclasses if target needs custom action types
6. No changes to `DeploymentTaskExecutor` required

---

## Deployment Pipeline — Detailed Flow

### ProcessAsync Top-Level Pipeline

```
ProcessAsync(serverTaskId, ct)                       — [DeploymentTaskExecutor.cs]
  try:
    LoadDeploymentDataAsync(serverTaskId, ct)         — [Prepare.cs]
    CreateTaskActivityNodeAsync(ct)                   — [Logging.cs]
    PrepareAllTargetsAsync(ct)                        — [Prepare.cs]
    PrepareCalamariIfRequiredAsync(ct)                — [Calamari.cs]
    ExecuteDeploymentStepsAsync(ct)                   — [Execute.cs]
    RecordSuccessAsync(ct)                            — [Logging.cs]
  catch:
    RecordFailureAsync(serverTaskId, ex, ct)          — [Logging.cs] → rethrow
```

### Phase 1: Load Deployment Data

```
LoadDeploymentDataAsync(serverTaskId, ct)             — [Prepare.cs]
  ├─ LoadTaskAsync                    — Load ServerTask, transition Pending → Executing
  ├─ LoadDeploymentAsync              — Load Deployment + Release
  ├─ LoadSelectedPackagesAsync        — Load package selections for release
  ├─ LoadOrSnapshotAsync              — Load process snapshot (or create from current Release)
  ├─ ResolveVariablesAsync            — Load variables from snapshot → _ctx.Variables
  ├─ FindTargetsAsync                 — Resolve target Machines by environment + enabled
  ├─ ConvertSnapshotToSteps           — Convert process snapshot → List<DeploymentStepDto>
  └─ PreFilterTargetsByRoles          — Remove targets with no matching step roles
```

### Phase 2: Prepare Targets (Per-Target)

```
PrepareAllTargetsAsync(ct)                            — [Prepare.cs]
  └─ foreach target in _ctx.AllTargets:
     ├─ LoadAccountForTarget
     │   ├─ Parse CommunicationStyle from endpoint JSON
     │   ├─ Resolve IEndpointVariableContributor (FirstOrDefault matching style)
     │   └─ Resolve IExecutionStrategy (FirstOrDefault matching style)
     ├─ LoadAccountCredentialsAsync
     │   └─ contributor.ParseAccountId(endpointJson) → resolve DeploymentAccount
     ├─ ContributeEndpointVariablesForTarget
     │   └─ contributor.ContributeVariables(endpointJson, account) → tc.EndpointVariables
     └─ ContributeAdditionalVariablesForTargetAsync
         └─ contributor.ContributeAdditionalVariablesAsync(snapshot, release, ct)
     → _ctx.AllTargetsContext[i] fully populated
```

### Phase 3: Execute Steps (Per-Step × Per-Target)

```
ExecuteDeploymentStepsAsync(ct)                       — [Execute.cs]
  ├─ Batch steps by StartTrigger
  └─ foreach batch:
     └─ ExecuteStepAcrossTargetsAsync(step, ct)
        └─ foreach matching target:
           ├─ ShouldExecuteStep                       — [Filter.cs]
           │   ├─ IsDisabled check
           │   ├─ EvaluateCondition (Success/Failure/Always/Variable)
           │   └─ MatchesTargetRoles (step roles ∩ machine roles)
           ├─ BuildEffectiveVariables
           │   └─ _ctx.Variables + tc.EndpointVariables
           ├─ PrepareStepActionsAsync
           │   └─ foreach action:
           │      ├─ ShouldExecuteAction (disabled, environment, channel)
           │      ├─ IActionHandlerRegistry.Resolve(action)
           │      ├─ ExpandActionProperties (variable substitution on properties)
           │      ├─ IActionHandler.PrepareAsync(context)
           │      │   → ActionExecutionResult { ScriptBody, Files, CalamariCommand }
           │      ├─ ExpandString (variable substitution on script body)
           │      └─ WrapScriptIfApplicable
           │          └─ IScriptContextWrapper.WrapScript (only for direct scripts)
           ├─ ExecuteActionResultsAsync
           │   └─ foreach prepared action:
           │      ├─ BuildScriptExecutionRequest
           │      ├─ IExecutionStrategy.ExecuteScriptAsync(request, ct)
           │      │   → ScriptExecutionResult { Success, LogLines, ExitCode }
           │      ├─ CaptureOutputVariables (parse ##octopus[setVariable] from logs)
           │      └─ Update activity node status
           └─ MergeStepResult
               └─ _ctx.Variables += outputVariables, _ctx.FailureEncountered |= failed
```

### Step Condition Evaluation

| Condition | Behavior |
|-----------|----------|
| `"Success"` (default) | Execute only if no prior step failure |
| `"Failure"` | Execute only if prior step failure occurred |
| `"Always"` | Execute regardless of prior failures |
| `"Variable"` | Stub — treated as Always (future: `EvaluateTruthy()`) |

`FailureEncountered` is a **sticky cumulative flag** — once set, remains true for all subsequent steps.

---

## Execution Strategies

### IExecutionStrategy Interface

```csharp
bool CanHandle(string communicationStyle);
Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct);
```

Resolved per-target via `_executionStrategies.FirstOrDefault(s => s.CanHandle(style))`.

### KubernetesAgentExecutionStrategy (Halibut Polling)

**Handles:** `"KubernetesAgent"`

**Flow:**
1. `ParseMachineEndpoint(machine)` — If `Uri` is empty + `PollingSubscriptionId` exists → `poll://{subscriptionId}/`
2. `_halibutClientFactory.CreateClient(endpoint)` → `IAsyncScriptService`
3. If `CalamariCommand != null` → `ExecuteCalamariViaHalibutAsync` (template `DeployByCalamari.ps1`, pack files)
4. Else → `ExecuteDirectScriptViaHalibutAsync` (user script + files as ScriptFile array)
5. `scriptClient.StartScriptAsync(command)` → `ScriptTicket`
6. `ObserveAndCompleteAsync` — polling loop:
   - Poll `GetStatusAsync` every 1 second
   - Collect logs from `statusResponse.Logs`
   - Timeout: 3 minutes
   - On complete: `CompleteScriptAsync` → final logs + exit code

**File Transfer:** Files packed as `ScriptFile[]` in `StartScriptCommand`. Variables serialized to `variables.json` + encrypted `sensitiveVariables.json`.

### KubernetesApiExecutionStrategy (Local Execution)

**Handles:** `"KubernetesApi"`

**Flow:**
1. Create temp work directory
2. Write `request.Files` to disk
3. If Calamari: template `DeployByCalamari.ps1`, run via `pwsh`
4. If direct: write `script.sh`, run via `bash`
5. Capture stdout/stderr → `ScriptExecutionResult`
6. Cleanup temp directory

---

## Halibut Polling Infrastructure

### Server Side

| Component | File | Purpose |
|-----------|------|---------|
| `HalibutModule` | `Halibut/HalibutModule.cs` | Autofac module: builds `HalibutRuntime` from `SelfCertSetting`, starts polling listener |
| `PollingListenerSetting` | `Settings/Halibut/PollingListenerSetting.cs` | Config: `Port=10943`, `Enabled=true`. When enabled → `halibutRuntime.Listen(port)` |
| `HalibutTrustInitializer` | `Halibut/HalibutTrustInitializer.cs` | `IStartable`: at startup queries all active polling machines (`PollingSubscriptionId != NULL AND !IsDisabled`), calls `halibutRuntime.Trust(thumbprint)` for each |
| `IHalibutClientFactory` | `Halibut/IHalibutClientFactory.cs` | Creates `IAsyncScriptService` client: `HalibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint)` |

### Agent Side (Production Tentacle Pod)

- Agent Pod starts own `HalibutRuntime` with `DelegateServiceFactory` registering `IScriptService`
- Calls `agentRuntime.Poll(pollUri, serverEndpoint, ct)` to initiate outbound connection
- Server queues `StartScriptCommand` when agent polls
- Agent executes script locally (kubectl/bash), returns results via `GetStatus`/`CompleteScript`

### Halibut 8.x API Notes

- `DelegateServiceFactory.Register<TContract, TAsyncContract>(Func<TAsyncContract>)` — requires both sync + async type params
- `ServiceEndPoint(Uri, string thumbprint, HalibutTimeoutsAndLimits)` — always requires 3 params
- `HalibutRuntime.Poll(Uri, ServiceEndPoint, CancellationToken)` — requires 3 params
- No `HalibutRuntime.GetCertificate()` method — reconstruct cert from `SelfCertSetting.Base64`

---

## Variable Substitution Pipeline

```
Stage 1: Load Base Variables
  └─ ResolveVariablesAsync → _ctx.Variables (from DB/snapshot)

Stage 2: Contribute Endpoint Variables (per-target)
  ├─ contributor.ContributeVariables(endpointJson, account)
  │   → 13+ K8s variables: ClusterUrl, Token, Namespace, SkipTlsVerification, etc.
  └─ contributor.ContributeAdditionalVariablesAsync(snapshot, release)
      → ContainerImage = "{feedUri}/{packageId}:{releaseVersion}"

Stage 3: Build Effective Variables
  └─ effectiveVariables = _ctx.Variables + tc.EndpointVariables

Stage 4: Expand Action Properties
  └─ VariableExpander.ExpandString(propertyValue, variableDictionary)
      → "#{Namespace}" becomes "production"

Stage 5: Expand Script Body
  └─ VariableExpander.ExpandString(scriptBody, variableDictionary)

Stage 6: Serialize for Execution
  ├─ Non-sensitive → variables.json (plaintext)
  └─ Sensitive → sensitiveVariables.json (AES encrypted, Calamari-compatible)

Stage 7: Capture Output Variables (post-execution)
  └─ Parse logs for ##octopus[setVariable name='X' value='Y' sensitive='True/False']
      → Add to _ctx.Variables as "Squid.Action.{StepName}.{VarName}" + unqualified
```

---

## Action Handler System

### IActionHandler Interface

```csharp
string ActionType { get; }
bool CanHandle(DeploymentActionDto action);
Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct);
```

`ActionHandlerRegistry` resolves via `_handlers.FirstOrDefault(h => h.CanHandle(action))`.

### Handlers

| Handler | ActionType | ScriptBody | Files | CalamariCommand |
|---------|-----------|------------|-------|-----------------|
| `RunScriptActionHandler` | `Squid.Script` | User script from properties | Empty | `null` |
| `KubernetesDeployYamlActionHandler` | `Squid.KubernetesDeployRawYaml` | `kubectl apply -f inline-deployment.yaml` | `{"inline-deployment.yaml": yamlBytes}` | `null` |
| `HelmUpgradeActionHandler` | `Squid.HelmChartUpgrade` | `helm upgrade --install ...` | `{"rawYamlValues.yaml": valuesBytes}` (if values) | `null` |
| `KubernetesDeployContainersActionHandler` | `Squid.KubernetesDeployContainers` | `kubectl apply -f .` | `{deployment.yaml, service.yaml, configmap.yaml, ...}` | `null` |

### Script Context Wrapping

`IScriptContextWrapper.WrapScript` only applies when `CalamariCommand == null` (direct scripts).

- `KubernetesApiScriptContextWrapper` — matches `"KubernetesApi"`, wraps script with kubectl context setup (cluster URL, auth token/cert, namespace, TLS config)
- `KubernetesAgent` — **no wrapper** (agent already has kubeconfig context in its Pod)

---

## Endpoint Variable Contributor

### KubernetesApiEndpointVariableContributor

**Contributed Variables (13+):**

| Variable | Source | Sensitive |
|----------|--------|-----------|
| `Squid.Action.Kubernetes.ClusterUrl` | endpoint | No |
| `Squid.Account.AccountType` | account | No |
| `Squid.Account.Token` | account | Yes |
| `Squid.Account.Username` | account | No |
| `Squid.Account.Password` | account | Yes |
| `Squid.Account.ClientCertificateData` | account | Yes |
| `Squid.Account.ClientCertificateKeyData` | account | Yes |
| `Squid.Account.AccessKey` | account | No |
| `Squid.Account.SecretKey` | account | Yes |
| `Squid.Action.Kubernetes.SkipTlsVerification` | endpoint | No |
| `Squid.Action.Kubernetes.Namespace` | endpoint | No |
| `Squid.Action.Kubernetes.ClusterCertificate` | endpoint | No |
| `ContainerImage` | async (feed + package + version) | No |

---

## DeploymentTaskContext — Shared Pipeline State

### Core Context (`_ctx`)

| Group | Property | Type | Purpose |
|-------|----------|------|---------|
| **Task** | `Task` | ServerTask | Current server task |
| | `Deployment` | Deployment | Current deployment |
| | `Release` | Release | Release entity |
| **Process** | `ProcessSnapshot` | DeploymentProcessSnapshotDto | Process definition |
| | `Steps` | List\<DeploymentStepDto\> | Converted step DTOs |
| | `SelectedPackages` | List\<ReleaseSelectedPackage\> | Package versions |
| **Variables** | `Variables` | List\<VariableDto\> | Deployment-level variables |
| **Targets** | `AllTargets` | List\<Machine\> | All matched machines |
| | `AllTargetsContext` | List\<DeploymentTargetContext\> | Per-target state |
| **Execution** | `FailureEncountered` | bool | Sticky cumulative failure flag |
| | `CalamariPackageBytes` | byte[] | Downloaded Calamari package |
| **Logging** | `TaskActivityNode` | ActivityLog | Root activity log node |

### Per-Target Context (`DeploymentTargetContext`)

| Property | Purpose |
|----------|---------|
| `Machine` | Target machine entity |
| `Account` | Resolved DeploymentAccount |
| `EndpointJson` | Machine endpoint JSON |
| `CommunicationStyle` | Parsed style (`"KubernetesApi"` / `"KubernetesAgent"`) |
| `ResolvedContributor` | IEndpointVariableContributor for this target |
| `ResolvedStrategy` | IExecutionStrategy for this target |
| `EndpointVariables` | Isolated endpoint variables (not polluting global) |

---

## Partial Class Organization

| File | Concern | Key Methods |
|------|---------|-------------|
| `DeploymentTaskExecutor.cs` | Interface, constructor, `_ctx`, `ProcessAsync` pipeline | `ProcessAsync` |
| `.Prepare.cs` | Data loading & target preparation | `LoadDeploymentDataAsync`, `PrepareAllTargetsAsync`, `LoadAccountForTarget`, `ContributeEndpointVariablesForTarget` |
| `.Execute.cs` | Step/action orchestration | `ExecuteDeploymentStepsAsync`, `PrepareStepActionsAsync`, `ExecuteActionResultsAsync`, `BuildEffectiveVariables`, `WrapScriptIfApplicable` |
| `.Filter.cs` | Pure static filtering | `ShouldExecuteStep`, `ShouldExecuteAction`, `EvaluateCondition`, `MatchesTargetRoles` |
| `.Script.cs` | Output variable extraction | `CaptureOutputVariables` |
| `.Logging.cs` | Activity logs, completion | `CreateTaskActivityNodeAsync`, `RecordSuccessAsync`, `RecordFailureAsync`, `PersistTaskLogAsync` |
| `.Calamari.cs` | Calamari package management | `PrepareCalamariIfRequiredAsync`, `DownloadCalamariPackageAsync` |
| `DeploymentTaskContext.cs` | Shared state class | Properties grouped by concern |

---

## Key Squid Source Files

| File | Purpose |
|------|---------|
| `Services/DeploymentExecution/DeploymentTaskExecutor*.cs` | Pipeline orchestrator (7 partial files) |
| `Services/DeploymentExecution/DeploymentTaskContext.cs` | Shared pipeline state |
| `Services/DeploymentExecution/IExecutionStrategy.cs` | Strategy interface: `CanHandle`, `ExecuteScriptAsync` |
| `Services/DeploymentExecution/IEndpointVariableContributor.cs` | `CanHandle`, `ParseAccountId`, `ContributeVariables`, `ContributeAdditionalVariablesAsync` |
| `Services/DeploymentExecution/IScriptContextWrapper.cs` | `CanWrap`, `WrapScript` |
| `Services/DeploymentExecution/IActionHandler.cs` | `ActionType`, `CanHandle`, `PrepareAsync` |
| `Services/DeploymentExecution/ActionHandlerRegistry.cs` | Handler resolution via `FirstOrDefault` |
| `Services/DeploymentExecution/Kubernetes/KubernetesAgentExecutionStrategy.cs` | Halibut polling: `ParseMachineEndpoint`, `ObserveAndCompleteAsync` |
| `Services/DeploymentExecution/Kubernetes/KubernetesApiExecutionStrategy.cs` | Local script execution in temp dir |
| `Services/DeploymentExecution/Kubernetes/KubernetesApiEndpointVariableContributor.cs` | 13+ K8s variables from endpoint JSON |
| `Services/DeploymentExecution/Kubernetes/KubernetesApiScriptContextWrapper.cs` | kubectl context wrapping (only for KubernetesApi) |
| `Services/DeploymentExecution/Handlers/RunScriptActionHandler.cs` | Returns user script, no files |
| `Services/DeploymentExecution/Kubernetes/KubernetesDeployYamlActionHandler.cs` | Inline YAML → files dict + kubectl apply |
| `Halibut/HalibutModule.cs` | Server HalibutRuntime builder + polling listener |
| `Halibut/HalibutTrustInitializer.cs` | Startup: trust all polling machine thumbprints |
| `Halibut/IHalibutClientFactory.cs` | Creates Halibut RPC client for agent communication |
| `Settings/Halibut/PollingListenerSetting.cs` | `Port=10943`, `Enabled=true` |
| `Services/Tentacle/IScriptService.cs` | Sync interface: `StartScript`, `GetStatus`, `CompleteScript`, `CancelScript` |
| `Commands/Tentacle/ScriptServiceCommands.cs` | Full command model: `StartScriptCommand`, `ScriptTicket`, `ScriptFile`, `ScriptStatusResponse`, `ProcessOutput`, `ProcessState` |

### Data Models

| Model | Key Fields |
|-------|------------|
| `ScriptExecutionRequest` | `ScriptBody`, `Files`, `CalamariCommand`, `Variables`, `Machine`, `CalamariPackageBytes` |
| `ScriptExecutionResult` | `Success`, `LogLines`, `ExitCode` |
| `ActionExecutionResult` | `ScriptBody`, `Files`, `CalamariCommand`, `Syntax`, `OutputVariables` |
| `ActionExecutionContext` | `Step`, `Action`, `Variables`, `ReleaseVersion` |
| `StartScriptCommand` | `ScriptBody`, `Isolation`, `Timeout`, `Files` (ScriptFile[]) |

---

## Test-First Development

Every behavioral change follows Red-Green-Refactor:
1. **Define test cases first** — cover all branches, edge cases, and expected log/output content
2. **Write failing tests** — tests compile but fail (red phase)
3. **Implement minimum code** to make tests pass (green phase)
4. **Refactor** — clean up while keeping tests green

Rules:
- No production logic change without a corresponding test
- Pure logic (evaluators, parsers, builders) → unit test
- Pipeline orchestration (logging scope, message content) → acceptance test with mocked services
- New enum/result types → test each variant

---

## Known Gaps (vs Octopus)

| Feature | Status | Notes |
|---------|--------|-------|
| ExcludedEnvironments DB persistence | Pending | DTO + filtering logic complete, but no entity/provider yet |
| Variable Condition evaluation | Stub | `"Variable"` condition treated as Always |
| Parallel steps (`StartWithPrevious`) | Not supported | Octopus allows steps to start concurrently |
| Tenant tag filtering | Not supported | Octopus filters actions by tenant tags |
| Manual skip (`SkipActions`) | Not supported | Octopus allows skipping specific actions at deploy time |

---

## Local Octopus Source Path

```
/Users/mars/Projects/octopus/OctopusDeploy/source/
```

---

## Test Architecture

### E2E Test Directory Structure

```
tests/Squid.E2ETests/
├── Infrastructure/                          — Shared test infrastructure
│   ├── E2EFixtureBase.cs                    — Core: DI + isolated DB + cert + lifecycle
│   ├── KindClusterFixture.cs                — Kind cluster create/reuse/delete
│   ├── KindClusterCollection.cs             — [CollectionDefinition("KindCluster")]
│   ├── TentacleStub.cs                      — In-process Halibut polling agent
│   ├── TentacleStub.ScriptRunner.cs         — IScriptService bash execution
│   ├── CapturingExecutionStrategy.cs        — Mock: records ScriptExecutionRequests
│   ├── CapturingLogSink.cs                  — Serilog sink: captures log messages
│   └── CapturingHalibutClientFactory.cs     — Mock: captures agent file transfers
├── Helpers/
│   ├── K8sTestDataSeeder.cs                 — Seeds complete K8s deployment scenario
│   └── YamlExtractor.cs                     — Extracts YAML from captured requests
├── Deployments/
│   ├── DeploymentPipelineFixture.cs         — Fixture: CapturingExecutionStrategy + dummy Calamari
│   └── Kubernetes/
│       ├── Agent/                           — Real Halibut polling tests
│       │   ├── KubernetesAgentE2EFixture.cs — Fixture: polling listener + TentacleStub + log capture
│       │   └── KubernetesAgentE2ETests.cs   — 5 tests: echo, exit code, kubectl, configmap, file transfer
│       ├── Api/                             — Direct KubernetesApi handler tests
│       │   ├── KubernetesApiE2ETestBase.cs  — Base: GetClusterUrl, GetToken, ExecuteBashScript helpers
│       │   ├── KubernetesContextScriptE2ETests.cs
│       │   ├── KubernetesDeployYamlE2ETests.cs
│       │   ├── HelmUpgradeE2ETests.cs
│       │   └── KubernetesFailureE2ETests.cs
│       └── Pipeline/                        — Full pipeline with capturing (both styles)
│           ├── KubernetesContainersDeployE2ETests.cs
│           ├── KubernetesMultiTargetE2ETests.cs
│           ├── KubernetesStepConditionE2ETests.cs
│           └── KubernetesVariableSubstitutionE2ETests.cs
└── GlobalUsings.cs
```

### Test Fixture Hierarchy

```
KindClusterFixture (ICollectionFixture — shared across [Collection("KindCluster")])
  └─ Manages Kind cluster lifecycle (create/reuse/delete)
  └─ Provides KubectlAsync(args) for kubectl operations

E2EFixtureBase<TTestClass> (IAsyncLifetime — per-test-class)
  ├─ Creates isolated Postgres DB (squid_e2e_{testclassname})
  ├─ Runs DbUp migrations
  ├─ Builds Autofac container with SquidModule
  ├─ Self-signed cert for Halibut
  ├─ Provides Run<T>(async action) for DI scope resolution
  │
  ├─ DeploymentPipelineFixture<TTestClass>
  │     ├─ RegisterOverrides: injects CapturingExecutionStrategy as sole IExecutionStrategy
  │     ├─ OnInitializedAsync: creates dummy Calamari.0.0.1-test.nupkg
  │     └─ Exposes ExecutionCapture property for test assertions
  │
  └─ KubernetesAgentE2EFixture<TTestClass>
        ├─ RegisterOverrides: enables PollingListenerSetting on random port
        ├─ OnInitializedAsync: creates TentacleStub, trusts stub cert in HalibutRuntime
        ├─ Exposes Stub (TentacleStub) and LogSink (CapturingLogSink)
        └─ OnDisposingAsync: disposes TentacleStub, cleans Calamari cache
```

### Three E2E Test Patterns

#### Pattern 1: Api Tests (KubernetesApiE2ETestBase inheritance)

Tests K8s API handlers **directly** — no deployment pipeline, no database. Uses `KubernetesApiContextScriptBuilder.WrapWithContext()` and executes bash scripts on real Kind cluster.

```csharp
public class KubernetesDeployYamlE2ETests : KubernetesApiE2ETestBase
{
    public KubernetesDeployYamlE2ETests(KindClusterFixture cluster) : base(cluster) { }

    [Fact]
    public async Task DeployYaml_InlineConfigMap_Bash_AppliesSuccessfully()
    {
        var clusterUrl = await GetClusterUrlAsync();           // from base
        var token = await GetServiceAccountTokenAsync();        // from base
        var result = await _yamlHandler.PrepareAsync(ctx, ct);  // direct handler call
        var scriptResult = await ExecuteBashScriptAsync(script); // from base
        scriptResult.ExitCode.ShouldBe(0);
    }
}
```

**Verifies:** Handler output, context script wrapping, kubectl operations, failure modes.

#### Pattern 2: Pipeline Tests (DeploymentPipelineFixture composition)

Tests the **full deployment pipeline** with CapturingExecutionStrategy — seeds DB, runs `ProcessAsync`, inspects captured scripts/files, then applies to Kind cluster.

```csharp
public class KubernetesContainersDeployE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesContainersDeployE2ETests>>
{
    [Theory]
    [InlineData("KubernetesApi", true)]
    [InlineData("KubernetesAgent", false)]
    public async Task FullPipeline_DeployContainers(string communicationStyle, bool createFeedSecrets)
    {
        var serverTaskId = await SeedDatabaseAsync(...);        // K8sTestDataSeeder
        await _fixture.Run<IDeploymentTaskExecutor>(executor =>
            executor.ProcessAsync(serverTaskId, ct));           // full pipeline
        var yamlFiles = YamlExtractor.Extract(ExecutionCapture); // inspect captured
        await _cluster.KubectlAsync($"apply -f ...");          // verify on cluster
    }
}
```

**Verifies:** Variable substitution, step condition evaluation, multi-target dispatch, role filtering, YAML generation, DB state transitions. Tests **both** KubernetesApi and KubernetesAgent communication styles via `[InlineData]`.

#### Pattern 3: Agent Tests (KubernetesAgentE2EFixture composition)

Tests **real Halibut polling** end-to-end — TentacleStub connects to Squid server's polling listener, receives commands via RPC, executes bash scripts on Kind cluster.

```csharp
public class KubernetesAgentE2ETests
    : IClassFixture<KubernetesAgentE2EFixture<KubernetesAgentE2ETests>>
{
    [Fact]
    public async Task Agent_RunScript_EchoOutput_Success()
    {
        var serverTaskId = await SeedRunScriptAsync("echo 'hello'"); // DB seed with polling machine
        await ExecutePipelineAsync(serverTaskId);                     // real Halibut RPC
        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("hello").ShouldBeTrue();    // log capture assertion
    }
}
```

**Verifies:** Polling listener startup, HalibutTrustInitializer, `poll://` URI construction, Halibut TLS RPC, DelegateServiceFactory → IScriptService dispatch, StartScript → GetStatus polling → CompleteScript protocol, real bash execution, DataStream file transfer.

### TentacleStub — In-Process Polling Agent

Simulates a K8s Tentacle Pod in the test process:

```
TentacleStub(serverThumbprint, serverPollingPort, kubeconfigPath)
  ├─ CreateSelfSignedCert() → agent cert + thumbprint
  ├─ ScriptRunner(kubeconfigPath) → IScriptService implementation
  ├─ AsyncScriptServiceAdapter(scriptRunner) → wraps sync → async
  ├─ DelegateServiceFactory.Register<IScriptService, IAsyncScriptService>()
  ├─ HalibutRuntimeBuilder → agent HalibutRuntime
  ├─ agentRuntime.Trust(serverThumbprint)
  └─ agentRuntime.Poll(poll://{subscriptionId}/, serverEndpoint, ct)
```

**ScriptRunner (IScriptService):**
- `StartScript`: creates temp dir → writes `script.sh` + additional files → starts `bash` process → async output capture
- `GetStatus`: drains `ConcurrentQueue<ProcessOutput>` → returns logs + Running/Complete state
- `CompleteScript`: waits process exit (30s) → final logs → cleanup temp dir
- `CancelScript`: kills process tree → cleanup
- Environment: `KUBECONFIG={kubeconfigPath}`, `PATH` includes `~/bin`, `/usr/local/bin`, `/opt/homebrew/bin`

### CapturingExecutionStrategy — Pipeline Mock

```csharp
public class CapturingExecutionStrategy : IExecutionStrategy
{
    public List<ScriptExecutionRequest> CapturedRequests { get; }
    public Func<ScriptExecutionRequest, ScriptExecutionResult> ResultFactory { get; set; }

    bool CanHandle(string style) => true;                    // universal handler
    Task<ScriptExecutionResult> ExecuteScriptAsync(request)  // records + returns
    {
        CapturedRequests.Add(request);
        return ResultFactory?.Invoke(request) ?? new ScriptExecutionResult { Success = true };
    }
}
```

Tests inspect `CapturedRequests` for: `ScriptBody`, `Files` (YAML), `Variables`, `Machine.Name`. `ResultFactory` allows simulating failures per-target for multi-target and step condition tests.

### K8sTestDataSeeder — Entity Graph

Seeds a complete deployment scenario:

```
VariableSet → Variables (Namespace, Replicas, AppEnv, DbHost)
  └─ Project → DeploymentProcess → DeploymentStep → DeploymentAction
       │         └─ StepProperties (TargetRoles)    └─ ActionProperties (full K8s config)
       │                                             └─ ActionMachineRoles
       ├─ Channel
       ├─ Environment
       ├─ Machine (KubernetesApi or KubernetesAgent endpoint JSON)
       ├─ DeploymentAccount (if KubernetesApi)
       ├─ ExternalFeed (DockerHub, if feed secrets)
       ├─ Release → ReleaseSelectedPackage
       ├─ Deployment (links project, environment, release, channel)
       └─ ServerTask (State=Pending)
```

Optional params: `agentSubscriptionId`, `agentThumbprint` — for Agent tests to plant real stub IDs.

### E2E Test Lifecycle Example

```
1. xUnit creates KindClusterFixture (once per collection)
   └─ kind create cluster --name squid-e2e (or reuse if exists)

2. xUnit creates DeploymentPipelineFixture<TestClass>
   └─ E2EFixtureBase.InitializeAsync()
       ├─ Create DB: squid_e2e_{testclassname}
       ├─ Run DbUp migrations
       ├─ Build Autofac container (SquidModule + overrides)
       └─ Generate self-signed Halibut cert
   └─ RegisterOverrides: inject CapturingExecutionStrategy
   └─ OnInitializedAsync: create dummy Calamari package

3. Test executes
   ├─ SeedDatabaseAsync → _fixture.Run<IRepository, IUnitOfWork>(K8sTestDataSeeder.SeedAsync)
   ├─ ExecutePipeline → _fixture.Run<IDeploymentTaskExecutor>(executor.ProcessAsync)
   │   └─ CapturingExecutionStrategy records all requests (no real script execution)
   ├─ YamlExtractor.Extract(ExecutionCapture) → inspect generated YAML
   ├─ AssertTaskState → _fixture.Run<IServerTaskDataProvider>(query DB)
   └─ _cluster.KubectlAsync("apply -f ...") → verify YAML on real Kind cluster

4. xUnit disposes fixtures
   ├─ DeploymentPipelineFixture.OnDisposingAsync: cleanup Calamari cache
   ├─ E2EFixtureBase.DisposeAsync: DROP DATABASE, dispose container
   └─ KindClusterFixture.DisposeAsync: kind delete cluster (unless SQUID_KEEP_CLUSTER=true)
```

---

## Test Project Structure Rules

- **Infrastructure** (fixture, base class, collection) in `Infrastructure/`, separated from business tests. Shared helpers in `Helpers/`.
- **By business domain**: tests grouped by feature domain (e.g., `Deployments/`), not by test type. Subdirectories named by tested concern (`Agent/`, `Api/`, `Pipeline/`).
- **Namespace = folder path**: moving files requires namespace update. `GlobalUsings.cs` avoids repeated infrastructure imports.
- **Fixture provides `Run<T>`**: no extra TestBase wrapper needed. Tests use `_fixture.Run<T>()` for DI scope.
- **Naming conventions**: `*E2ETestBase` = abstract base class (inheritance), `*E2EFixture` = xUnit class fixture (composition via `IClassFixture<T>`). Prefix with target type (e.g., `KubernetesApi*`, `KubernetesAgent*`).

---

## C# Coding Conventions

Reference: SmartTalk `RealtimeAiServiceV2` pattern.

### Method Length

- Target **5-25 lines** per method. Max ~40 lines only for unavoidable orchestration.
- Every distinct piece of logic is its own named method — method name = intent.
- If a method has two `foreach` loops doing different things, split into two methods.

### Blank Lines

- **One blank line** between methods.
- **Blank line after variable declarations** before the first statement that uses them.
- **Blank line between logical blocks** within a method (fetch → guard → mutate → log = 4 blocks, separated by blank lines).
- **Blank line after guard clauses** (`if ... throw` / `if ... return`).
- **Blank line before `return`** when return follows a logical block. No blank line for trivial returns.
- **No blank line** between properties in the same group (context class style).

```csharp
var task = await _provider.GetByIdAsync(id, ct).ConfigureAwait(false);

if (task == null)
    throw new InvalidOperationException($"Task {id} not found");

_ctx.Task = task;

Log.Information("Loaded task {TaskId}", id);
```

### Guard Clauses

- **Same-line** for trivial guards (`return;`, `return value;`, `continue;`, `break;`):
  ```csharp
  if (x == null) return;
  if (!isActive) return;
  ```
- **Next-line, no braces** for `throw` or longer single-statement guards:
  ```csharp
  if (task == null)
      throw new InvalidOperationException($"Task {id} not found");
  ```
- **Braces** for multi-statement bodies:
  ```csharp
  if (handler == null)
  {
      Log.Warning("No handler for {Type}, skipping", action.ActionType);
      continue;
  }
  ```

### Line Breaking

- Wrap method arguments **when line exceeds ~120 characters**: each argument on its own line, indented 4 spaces, closing `)` on the last argument line.
- `.ConfigureAwait(false)` on the **same line** as closing `)`.
- **Exception**: fluent chains put `.ConfigureAwait(false)` on its own indented line:
  ```csharp
  var vars = await _contributor
      .ContributeAdditionalVariablesAsync(snapshot, release, ct)
      .ConfigureAwait(false);
  ```
- **Top-level pipeline** methods (e.g., `ProcessAsync`, `ConnectAsync`) **omit** `.ConfigureAwait(false)` for readability. All internal methods use it on every `await`.

### Logging

- Serilog structured templates with `{Placeholder}` — **never** string interpolation in log templates.
- Single-parameter: one line. Multi-parameter: wrap when line is too long.
- Exception messages: **use** string interpolation (`$"Task {id} not found"`).

### Context Class (`_ctx`)

- All pipeline methods read/write `_ctx` directly — minimize parameter passing.
- Properties grouped by concern with `// Comment` headers, no blank lines within a group:
  ```csharp
  // Task & Deployment
  public ServerTask Task { get; set; }
  public Deployment Deployment { get; set; }

  // Targets
  public List<Machine> Targets { get; set; } = new();
  public Machine Target { get; set; }
  ```

### Partial Class Organization

One file per concern. Main file = interface + constructor + flat pipeline:

| File Suffix | Concern |
|-------------|---------|
| `.cs` | Interface, constructor, `_ctx` field, top-level pipeline (`ProcessAsync`) |
| `.Context.cs` or `Context.cs` | Shared state class with grouped properties |
| `.Prepare.cs` | Data loading / preparation steps |
| `.Execute.cs` | Orchestration loop (batch, execute, merge) |
| `.Filter.cs` | Pure static filter / predicate methods |
| `.Script.cs` | External script execution / observation |
| `.Logging.cs` | Activity logs, task logs, completion recording |
| `.Send.cs` | Outbound messaging (if applicable) |
| `.Event.cs` | Event handlers (if applicable) |

### Style

- File-scoped namespaces (`namespace Foo;`).
- Target-typed `new()` for collection initialization.
- `var` for all local variables.
- No `this.` prefix — fields use `_fieldName`.
- `static` methods for pure logic with no instance side effects.
- `.Replace("{{Key}}", value, StringComparison.Ordinal)` for template substitution.

## Development Rules

See `~/.claude/CLAUDE.md` for global rules (no breaking changes, design principles, refactoring patterns).
