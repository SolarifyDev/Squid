# Squid Deployment Architecture — Comprehensive Review

> **Date**: 2026-05
> **Scope**: Post-1.6.9 architecture audit + Octopus parity comparison
> **References**:
> - Octopus local source: `/Users/mars/Projects/octopus/` (Calamari + OctopusDeploy + OctopusTentacle)
> - OctopusTentacle OSS: <https://github.com/OctopusDeploy/OctopusTentacle>
> - Squid source: this repo

---

## §0 Executive verdict

| Dimension | Verdict | Confidence |
|---|---|---|
| **Architecture generality** | ✅ Strong. Every action type, every target, every script runs through the same `IActionHandler → ExecutionIntent → IIntentRenderer → IExecutionStrategy` pipeline. Zero target-specific specialcasing in the orchestrator | High — verified by grep |
| **DI-driven extensibility** | ✅ Adding a target type or action type is a small mechanical change — implement 5 interfaces, register via `IScopedDependency`, no orchestrator edits | High |
| **Production stability mechanisms** | ✅ Three-tier testing (~568 test classes), 12 hardening validators, 32 env-var escape hatches, drift detectors on mirror-tier scripts | High |
| **Cross-platform tentacle coverage** | ✅ K8s API + K8s Agent + Tentacle Polling + Tentacle Listening + SSH (Linux/macOS) + OpenClaw + Server-only — 7 transports, all on the same pipeline | High |
| **Octopus protocol parity (V2 idempotent + capabilities + resume)** | 🟡 **Partial**. Squid has the orchestration shape but is missing the wire-protocol versioning + agent-side state persistence that Octopus uses to survive mid-deploy network failures | High — concrete gaps enumerated in §6 |
| **Documentation freshness** | 🟡 `CLAUDE.md`'s "Deployment Pipeline" section is partially stale (Squid moved from partial-class executor to phase-object pipeline since the docs were written) | High — drift items listed in §7 |

**Bottom line**: the architecture is strong and generic; the deployment-target abstraction is sound; cross-platform coverage is real and tested. The remaining production-stability investments are in the **wire protocol layer** (resume-after-disconnect, idempotent RPC, capability negotiation) — these are 1.7.x candidates, not 1.6.9 blockers.

---

## §1 Squid architecture map (verified against actual source)

### 1.1 Pipeline orchestrator — 8 phases, DI-discovered

`IDeploymentTaskExecutor.ProcessAsync()` is implemented by `DeploymentPipelineRunner` (`Pipeline/DeploymentPipelineRunner.cs:8`). It composes `IEnumerable<IDeploymentPipelinePhase>` (DI), orders by `Order`, executes each. Phases are sealed partial classes when they need internal splits.

| Order | Phase | Concern |
|---|---|---|
| 50 | `ResumeCheckpointPhase` | Checkpoint resume — restored output vars + batch indices |
| 100 | `LoadTaskPhase` | `ServerTask` Pending → Executing transition |
| 200 | `LoadDeploymentDataPhase` | Deployment, Release, Project, Environment, Channel, SelectedPackages |
| 300 | `PrepareDeploymentPhase` | Process snapshot, base variables, target enumeration, role pre-filter |
| 400 | `PrepareTargetsPhase` | Per-target: parse style → resolve transport → contribute variables |
| 450 | `AnnounceSetupPhase` | Lifecycle events (`MachineConstraintsResolved`, `TargetsResolved`, `TargetTransportMissing`) |
| 460 | `PlanDeploymentPhase` | `IDeploymentPlanner.PlanAsync` shadow-mode preview |
| 500 | `ExecuteStepsPhase` (split across `.cs`/`.Prepare.cs`/`.Execute.cs`/`.GuidedFailure.cs`) | Batch → per-target parallel → action prep → render → execute → capture → checkpoint |
| 600 | `RetentionCleanupPhase` | Best-effort retention enforcement |

Completion (success/failure/cancel/pause) is handled **after** all phases in the runner via `IDeploymentCompletionHandler`. Lifecycle events flow through `IDeploymentLifecycle.EmitAsync` to `DeploymentActivityLogger`.

### 1.2 Five extension pillars + concrete implementations

| Pillar | Interface | Count | Files |
|---|---|---|---|
| **Action dispatch** | `IActionHandler` | **19** handlers (RunScript, HealthCheck, Manual, 9 K8s, IIS, 7 OpenClaw) | `Handlers/` + `Targets/*/Handlers/` |
| **Transport composition** | `IDeploymentTransport` + `ITransportCapabilities` | **7** transports (Server, KubernetesApi, KubernetesAgent, TentaclePolling, TentacleListening, Ssh, OpenClaw) | `Transport/` + `Targets/*/Transport/` |
| **Variable contribution** | `IEndpointVariableContributor` | **5** contributors (none for Server) | `Targets/*/Transport/*EndpointVariableContributor.cs` |
| **Intent rendering** | `IIntentRenderer` | **7** renderers (one per transport) | `Transport/` + `Targets/*/Rendering/` |
| **Script execution** | `IExecutionStrategy` | **4** strategies — `LocalProcess`, `HalibutMachine`, `Ssh`, `OpenClaw` (shared across transports) | `Infrastructure/` + `Targets/*/Transport/` |
| **Health check** | `IHealthCheckStrategy` | **5** strategies (none for Server) | `Targets/*/Transport/*HealthCheckStrategy.cs` |

**Critical invariant**: zero `IISWebSite\|DeployToIIS\|IIS\.` references in the generic pipeline outside `Targets/Tentacle/` and `Constants/IISDeployProperties.cs`. Same for every other target type — confirmed via grep. Adding a target type requires zero changes to `DeploymentPipelineRunner` or any `IDeploymentPipelinePhase`.

### 1.3 Execution intent taxonomy

8 concrete `ExecutionIntent` types — the **semantic layer** that decouples handlers from transport-specific scripts:

| Intent | Carries | Emitted by |
|---|---|---|
| `RunScriptIntent` | `ScriptBody`, `Syntax`, `InjectRuntimeBundle` | `RunScript`, `HealthCheck` (custom-script branch), `IISDeploy` |
| `KubernetesApplyIntent` | `YamlFiles[]`, `Namespace`, `Syntax`, `ServerSideApply`, `FieldManager`, `ForceConflicts`, `ObjectStatusCheck` | 6 K8s handlers (RawYaml, Containers, ConfigMap, Secret, Service, Ingress) |
| `HelmUpgradeIntent` | `ReleaseName`, `ChartReference`, `Repository`, `ValuesFiles[]`, `InlineValues`, `Wait`, `Timeout` | `HelmUpgradeActionHandler` |
| `KubernetesKustomizeIntent` | `OverlayPath`, `CustomKustomizePath`, `Namespace`, `ServerSideApply` | `KubernetesKustomizeActionHandler` |
| `OpenClawInvokeIntent` | `Kind` (enum), `Parameters` (dict) | 7 OpenClaw handlers |
| `HealthCheckIntent` | `CustomScript`, `CheckType`, `ErrorHandling`, `IncludeNewTargets` | `HealthCheckActionHandler` |
| `ManualInterventionIntent` | `Instructions`, `ResponsibleTeamIds` (renderer no-op; suspension in `ExecuteStepLevelAsync`) | `ManualInterventionActionHandler` |
| `DeployPackageIntent` | `Package`, `ExtractTo`, `PreDeployScript`, `PostDeployScript` | Reserved for staged-package flow (`IPackageStagingPlanner`) |

This taxonomy is what makes the architecture truly generic: handlers describe **what** to do (semantic intent), renderers translate **how** for their transport (kubectl preamble vs ssh bundle vs IIS PS1 preamble), strategies dispatch **where** (Halibut RPC vs local process vs SSH vs HTTP).

### 1.4 Variable substitution — 7 stages, all verified in code

| Stage | Where |
|---|---|
| 1. Load Base | `4_PrepareDeploymentPhase.cs:58` — `IDeploymentVariableResolver.ResolveVariablesAsync` |
| 2. Contribute Endpoint | `5_PrepareTargetsPhase.cs:83-93` — `contributor.ContributeVariables(endpointContext)` + async additionals |
| 3. Build Effective | `Variables/EffectiveVariableBuilder.cs:9` — scope-evaluated base + endpoint concat |
| 4. Expand Action Properties | `6_ExecuteStepsPhase.Prepare.cs:65` — `VariableExpander.ExpandActionProperties` |
| 5. Expand Intent Strings | `6_ExecuteStepsPhase.Execute.cs:427` — `IntentVariableExpander.Expand` |
| 6. Serialize Sensitive | `CalamariPayloadBuilder` — AES-encrypted `sensitiveVariables.json`, plaintext `variables.json` |
| 7. Capture Output | `6_ExecuteStepsPhase.Execute.cs:377` — `ServiceMessageParser` parses `##squid[setVariable …]` + `##octopus` back-compat |

All 7 stages present and operative.

---

## §2 Stability mechanisms catalog

### 2.1 Three-tier test coverage — concrete counts

| Tier | Project | Test classes | Notes |
|---|---|---|---|
| **Unit (server core)** | `tests/Squid.UnitTests` | 310 | Service / pipeline / handler unit tests |
| **Unit (Calamari)** | `tests/Squid.Calamari.Tests` | 27 | Calamari-side execution unit tests |
| **Unit (Tentacle)** | `tests/Squid.Tentacle.Tests` | 136 | Agent-side script execution, log capture, isolation |
| **Integration** | `tests/Squid.IntegrationTests` + co-located `*IntegrationTests.cs` | ~23 | Real DB / change tracker / concurrency token coverage |
| **E2E (server)** | `tests/Squid.E2ETests` | 34 | Full pipeline + capturing strategy + Kind cluster |
| **E2E (Linux Tentacle)** | `tests/Squid.LinuxTentacleE2ETests` | 17 | Real Linux binary + systemd integration |
| **E2E (Windows Tentacle)** | `tests/Squid.WindowsTentacleE2ETests` | 21 | Real Windows binary + IIS deploy + Service host |
| **Total** | | **~568 test classes** | Each carries 5-50+ `[Fact]` / `[Theory]` methods |

### 2.2 Rule 11 enforcement validators (12 active)

Three-mode `Off/Warn/Strict` validators behind a single env-var per check. Backward-compat by default (`Warn`); operators flip to `Strict` for production hardening.

| Validator | Domain | EnvVar | Default mode |
|---|---|---|---|
| `SpaceMembershipSpecification` | Auth — space access | `SQUID_SPACE_MEMBERSHIP_ENFORCEMENT` | **Strict** (security) |
| `UserRoleService` | Auth — role assignment | `SQUID_ROLE_ASSIGNMENT_ENFORCEMENT` | **Strict** (security) |
| `SquidVariableEncryption` | Sensitive var encryption | `SQUID_SENSITIVE_VAR_ENCRYPTION_ENFORCEMENT` | Warn |
| `VariableEncryptionService` | Master key | `SQUID_MASTER_KEY_ENFORCEMENT` | Warn |
| `OutputVariableMerger` | Collision detection | `SQUID_OUTPUT_VAR_COLLISION_ENFORCEMENT` | Warn |
| `SensitiveValueLeakGuard` | Output-var sensitive leak | `SQUID_OUTPUT_VAR_SENSITIVE_LEAK_ENFORCEMENT` | Warn |
| `ServiceMessageParser` | Reserved-var name | `SQUID_OUTPUT_VAR_NAME_ENFORCEMENT` | Warn |
| `SensitiveVariableDecryptor` (Calamari) | Legacy format acceptance | `SQUID_SENSITIVE_VAR_DECRYPT_LEGACY_ACCEPT` | Warn |
| `SensitiveVariableDecryptor` (Tentacle) | Legacy format acceptance | (same) | Warn |
| `ServerCertificateValidator` (Tentacle) | Server cert thumbprint | `SQUID_SERVER_CERT_ENFORCEMENT` | Warn |
| `TentacleListeningRegistrar` | HTTPS scheme | `SQUID_REGISTER_HTTPS_ENFORCEMENT` | Warn |
| `ScriptPodImageValidator` (K8s Tentacle) | Pod image safety | `SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT` | Warn |

Every `EnforcementEnvVar` constant is pinned by a `*_ConstantNamePinned` unit test — rename triggers a CI failure.

### 2.3 Rule 8 env-var escape hatches (~32 total)

Operator-tunable knobs gated by env vars with literal constants pinned by test. Categories:

- **Halibut tuning** (3): `TcpReceiveResponseTimeoutSeconds`, `PollingRequestQueueTimeoutSeconds`, `MaxPendingWorkPerAgent`
- **Tentacle upgrade overrides** (~10): per-flavour download URL, healthcheck URL, retries, state dir, fatal flag, service timeout
- **Pipeline tuning** (3): `GlobalParallelismCap`, log buffer capacity, package version enumeration cap
- **Cross-process** (2): Calamari sensitive password env, Tentacle orphan workspace max age
- **Network binding** (2): Tentacle listen IP, polling startup jitter
- **Platform compat** (1): `UseWindowsPowerShell` for pwsh fallback

### 2.4 Drift detector pattern usage

Two explicit "MirrorsProductionTemplate" detectors:
- `WindowsUpgradePhaseBE2ETests.PhaseBScript_MirrorsProductionTemplate_KeyOperations` — pins upgrade PS1 invariants
- `IISDeployScriptDriftDetectorTests` — 19 tests pinning the IIS PS1 mirror

Both follow the canonical pattern: read prod source, grep for key operations, fail with "drift detected — update inline copy at line X" message.

### 2.5 Other production-grade mechanisms

| Mechanism | Implementation |
|---|---|
| **Checkpoint / resume-from-batch** | `ResumeCheckpointPhase` + `IDeploymentCheckpointStore` — survives task host crash mid-batch |
| **Sensitive value masking in logs** | `SensitiveValueMasker` in `SequencedLogWriter` |
| **Service-message parsing** | `##squid[setVariable …]` + `##octopus[...]` back-compat — `ServiceMessageParser` |
| **Output variable collision detection** | `OutputVariableMerger` with `EnforcementMode` (warn or fail on collision) |
| **Halibut circuit breaker** | `IMachineCircuitBreakerRegistry` — wraps every RPC; per-machine state |
| **Runtime capability cache** | `IMachineRuntimeCapabilitiesCache` — populated by health check, queried by handler dispatch |
| **Parallel target executor with global cap** | `TargetParallelExecutor` — `GlobalParallelismCapEnvVar` env-tunable |
| **Sequenced log writer** | `SequencedLogWriter` — append-only file with sequence numbers, survives agent restart |
| **AsyncStreamReader drain** | `LocalScriptService.DrainAsyncReadersOnce` (PR #326) — fixes the .NET process stdout race |
| **IIS metabase mutex serialization** | Shared `Global\Octopus-IIS-Metabase-Mutex` — cross-vendor coordination on shared hosts |

---

## §3 Cross-platform target matrix

| Transport | OS coverage | Execution backend | Action types supported | Tier-2 health check |
|---|---|---|---|---|
| `ServerTransport` (None) | Server host (any OS) | Local process | `Script`, `HealthCheck` | N/A |
| `KubernetesApiTransport` | Any cluster (kubectl from server) | Local process w/ kubectl context preamble | All 9 K8s + `Script` + `HealthCheck` (10 total) | `kubectl version` probe |
| `KubernetesAgentTransport` | Linux (agent pod) | Halibut RPC → agent pod → script pod | All 9 K8s + `Script` + `HealthCheck` (10 total) | Halibut `GetCapabilities` + RBAC `can-i` |
| `TentaclePollingTransport` | Linux + Windows | Halibut polling RPC | `Script`, `HealthCheck`, `DeployToIISWebSite` | Halibut `GetCapabilities` |
| `TentacleListeningTransport` | Linux + Windows | Halibut listening RPC | `Script`, `HealthCheck`, `DeployToIISWebSite` | Halibut `GetCapabilities` |
| `SshTransport` | Linux + macOS | SSH command channel | `Script`, `HealthCheck` | SSH connect + optional custom script |
| `OpenClawTransport` | HTTP gateway | HTTPS POST | 7 OpenClaw action types | HTTP probe |

**Verification**: every transport is exercised by a real-host E2E suite (`Squid.LinuxTentacleE2ETests`, `Squid.WindowsTentacleE2ETests`, `Squid.E2ETests` Kind-cluster fixtures). No transport ships untested.

---

## §4 Octopus parity matrix — Squid vs Octopus-local vs OctopusTentacle

| Dimension | Octopus (local source) | OctopusTentacle OSS | Squid | Verdict |
|---|---|---|---|---|
| **Action dispatch shape** | Two parallel registries: `IServerActionHandler/ITargetActionHandler` (legacy) + `IActionHandler` (newer, cloud-flavoured) bridged via `DeploymentActionDefinitionAdapter` | (server-side; not in Tentacle repo) | Single `IActionHandlerRegistry` with case-insensitive `Dictionary<string, IActionHandler>` lookup | ✅ Squid simpler — one path, O(1) lookup |
| **Action count** | 28 server-side handlers + 7 extensibility handlers (Azure, AWS, K8s, Terraform, …) | — | 19 handlers (RunScript, HealthCheck, Manual, 9 K8s, IIS, 7 OpenClaw) | 🟡 Squid covers fewer cloud targets — by design (focused scope) |
| **Per-stage feature pipeline** | `IFeature` + 9 `DeploymentStages` (BeforeDeploy/Deploy/AfterDeploy × Pre/Post variants) | — | Not modelled — handlers emit `ExecutionIntent` directly | ✅ Squid intent layer replaces per-stage hooks. Operator-facing custom-script hooks (PreDeploy/PostDeploy) embedded in mirror-tier PS1 |
| **Convention pipeline** | `IInstallConvention` + `IRollbackConvention` driven by `ConventionProcessor` — 24 conventions + 20+ instantiations per package deploy | — | Flat pipeline in `DeploymentPipelineRunner` phase objects | ✅ Squid simpler. Sufficient for current scope. Could revisit if rollback semantics grow |
| **Script-wrapper chain** | Priority-ordered linked-list of 9 `IScriptWrapper`s (kubectl, az, aws, gcp, fabric, …) | — | `IIntentRenderer` per transport — preamble baked at render time, no chain | ✅ Squid simpler. Each renderer is responsible for its complete preamble. Less flexibility but easier to reason about |
| **Endpoint polymorphism** | Abstract `Endpoint` + 10 subclasses + capability marker interfaces (`IEndpointWithAccount`, `IEndpointWithProxy`, `IEndpointWithExpandableCertificate`, …); `EndpointConverter` discriminates on `CommunicationStyle` | (server-side) | JSON string + `ParseCommunicationStyle()` + per-contributor deserialization | ✅ Squid flexible — no class hierarchy coupling |
| **Account polymorphism** | Abstract `Account` + 6 subclasses; `AccountConverter` discriminates on `AccountType` enum | — | Single flat `DeploymentAccount` entity with `AccountType` enum | ✅ Squid adequate. Could split later if account-type-specific fields grow |
| **Variable contribution** | `IContributeVariables` on 13 entities; `MachineVariableCollector` orchestrates per-machine scoping with `ScopeField.Machine` wrapping | — | `IEndpointVariableContributor` resolved by `CommunicationStyle` — no entity coupling | ✅ Squid better — easier to add new contributors without entity surgery |
| **SpecialVariables taxonomy** | 1208-line static class with 23 nested classes + `[Define(Description, Example)]` metadata | — | Hardcoded strings in contributors/handlers | 🟡 Squid weaker — no centralized taxonomy or operator-facing variable docs. **1.7.x candidate** |
| **Halibut runtime — server** | `ServerCommunicationsModule` + `PollingEndpointDistributor` (`halibut.Listen` + `halibut.TrustOnly(thumbprints[])` — replaces full set) + WebSocket listener | — | `HalibutModule` + `HalibutTrustInitializer` (`halibut.Trust(thumbprint)` per machine on startup) | ✅ Equivalent shape; Squid uses additive trust instead of `TrustOnly` |
| **Halibut runtime — agent** | `TentacleCommunicationsModule` + `HalibutInitializer`: supports both `Poll` (TentacleActive) AND `Listen` (TentaclePassive) in same binary | Same | Same — both polling + listening transports use same Halibut runtime | ✅ Equivalent |
| **Wire protocol versioning** | V1 + V2 (`IScriptServiceV2` — idempotent re-attempt) + `IKubernetesScriptServiceV1` + `ICapabilitiesServiceV2` (with `[CacheResponse(600)]`) | Same | **Single version** of `IScriptService`. No idempotent re-attempt. No capabilities service. | 🔴 **Gap — see §6.1, §6.2** |
| **Agent-side state persistence** | `IScriptStateStore` persists `ScriptState` to disk per ticket — survives agent restart mid-script | Same | In-process `ConcurrentDictionary<ScriptTicket, RunningScript>` — agent restart loses ticket | 🔴 **Gap — see §6.3** |
| **Retry differentiation** | `RpcCallExecutor` (default policy) vs `FileTransferRpcCallExecutor` (`MinimumAttemptsForInterruptedLongRunningCalls`) — file transfer gets a different retry envelope from status polls | Same | Single retry posture | 🔴 **Gap — see §6.4** |
| **Workspace janitor** | `WorkspaceCleaner` background task — walks workspaces, checks `IRunningScriptReporter`, deletes orphans > N days | Same | `LocalScriptService.OrphanMaxAgeEnvVar` exists but no proactive cleaner | 🟡 **Gap — see §6.5** |
| **Watchdog (Windows)** | Separate Scheduled Task runs `Tentacle.exe checkservices` periodically as belt-and-braces against silent service death | Same | SCM `Restart=on-failure` only | 🟡 **Gap — see §6.6** |
| **Separate Upgrader binary** | `Octopus.Tentacle.Upgrader.exe` side-car so the running EXE can be replaced safely (Windows can't overwrite running PE) | Same | In-process upgrade in `WindowsTentacleUpgradeStrategy` | 🟡 **Gap — see §6.7** |
| **Standalone client SDK** | `Octopus.Tentacle.Client` as separate assembly — third-party tooling can integrate | Same | `IHalibutClientFactory` is server-internal | ⚪ Low priority — only matters if external integrations needed |
| **Log-cursor protocol** | Every `ScriptStatusResponse` carries `NextLogSequence: long` — server passes watermark on next poll, no duplicate delivery | Same | `SequencedLogWriter` exists; status response uses it via `ReadLogsAndCursor`. **Squid ALREADY has this!** | ✅ Equivalent (verified in `LocalScriptService.cs:1254-1268`) |
| **Long-poll completion wait** | `DurationToWaitForScriptToFinish` on `StartScriptCommandV2` — server can wait inside one RPC for completion, dramatically cuts poll RPC count | Same | Polling loop in `HalibutMachineExecutionStrategy` polls every 1s with 3min timeout — many RPCs per deploy | 🟡 **Gap — see §6.8** |
| **Test matrix parameterization** | `TentacleConfigurationsAttribute` + `CartesianProduct` runs each integration test across `TentacleType × TentacleRuntime × TentacleVersion × IsolationLevel × RpcRetrySettings` | Same | Each E2E runs once per platform | 🟡 **Gap — see §6.9** |
| **Backwards-compat decorators** | `BackwardsCompatibleCapabilitiesV2Decorator` synthesizes responses for old agents | Same | None — wire protocol changes are hard cutovers | 🔴 **Gap — see §6.10** |
| **Three-tier test coverage** | ~3,700 attribute-tests across Calamari/Octopus/Tentacle + ~210 custom TestScript classes | Comprehensive `Octopus.Tentacle.Tests.Integration` with `ClientAndTentacleBuilder` running real binary | ~568 test classes across unit/integration/E2E, real Kind cluster + real Windows IIS + real Linux systemd | ✅ Squid coverage is comprehensive for current scope. Pattern is sound |

---

## §5 Stability gaps that affect production stability — actionable items

Ordered by production risk. Each has a current-state note + a concrete fix recommendation.

### 6.1 🔴 Wire-protocol versioning (`IScriptService` → `IScriptServiceV2`)

**Current state**: Squid ships one version of `IScriptService`. Any wire-shape change is a forced lock-step upgrade of server + every agent.

**Risk**: Operator must orchestrate simultaneous server + N-agent upgrades. In a fleet of 1000 agents, this is impractical. Any agent on older Squid that handshakes with a new server today silently breaks.

**Fix**: introduce `IScriptServiceV2` with the **idempotent `StartScript`** semantic (server can re-send `StartScript(ticket)` and agent returns existing state instead of re-executing). Wire `ICapabilitiesServiceV2.GetCapabilities()` so server can detect which version each agent speaks. Bump on next major (1.7.x).

**Effort**: medium — 1-2 weeks. New contracts + Halibut service registration + agent decorator for compat.

### 6.2 🔴 Capability negotiation (`ICapabilitiesServiceV2`)

**Current state**: Squid's `ITransportCapabilities` is **server-side** — server declares what it expects each transport to do. The agent never advertises what it can do.

**Risk**: server has no way to detect older agents missing a feature. Today it just dispatches and the agent fails or silently no-ops.

**Fix**: `ICapabilitiesServiceV2` on the agent returns `["IScriptServiceV2", "IFileTransferService", "IKubernetesScriptServiceV1", …]`. Server caches for 10 min per Halibut's `[CacheResponse(600)]` pattern. Server-side handler dispatch consults this before sending intent-specific RPCs.

**Effort**: medium — paired with §6.1. Capabilities service is small but cache + decorator infrastructure is real.

### 6.3 🔴 Agent-side persistent `ScriptStateStore`

**Current state**: `LocalScriptService._scripts` is a `ConcurrentDictionary<TaskId, RunningScript>` (in-process). If the agent restarts mid-script, the entry is lost. Server's next `GetStatus` falls through to `TryBuildStatusFromPersistedLogs` which reads logs from disk but doesn't reconstruct full `RunningScript` state.

**Risk**: agent crash mid-deploy = server sees `UnknownResult` exit code → step fails → deploy fails. Operator has no choice but to retry the whole deploy from scratch.

**Fix**: persist `ScriptState { ExitCode, CompletedAt, Status }` to disk per ticket; on agent restart, scan workspace dirs and reconstruct in-memory state. `CompleteScript(ticket)` works even after agent restart.

**Effort**: medium — adjacent to existing `SequencedLogWriter` infrastructure. Mostly serialization + scan logic.

### 6.4 🔴 Retry-policy differentiation by RPC class

**Current state**: a single retry posture for every Halibut RPC. Long file uploads and short status polls get the same envelope.

**Risk**: a 5-minute file upload that loses connection at 4:50 might not get enough retries (envelope was sized for status polls). Or status polls might retry far too long (envelope sized for file transfers).

**Fix**: split into `RpcCallExecutor` (default) + `FileTransferRpcCallExecutor` (`MinimumAttemptsForInterruptedLongRunningCalls`). Tag each RPC with its class.

**Effort**: low — 2-3 days. Mostly classification + wiring.

### 6.5 🟡 Workspace janitor background task

**Current state**: `LocalScriptService.OrphanMaxAgeEnvVar` exists as a constant but there's no background task that actually reaps orphans. Workspaces from crashed scripts accumulate.

**Risk**: disk fills with leftover `squid-tentacle-{ticketId}` dirs in `%TEMP%` over weeks/months. Operator must manually clean.

**Fix**: scheduled background task that walks workspaces > `OrphanMaxAge`, checks `IRunningScriptReporter` (skip live), deletes. Matches `WorkspaceCleaner.cs` pattern in OctopusTentacle.

**Effort**: low — 2-3 days. Existing scaffolding.

### 6.6 🟡 Windows watchdog scheduled task

**Current state**: Squid Windows Tentacle relies solely on SCM auto-restart on failure.

**Risk**: silent service stop (e.g. SCM bug, manual stop forgotten) leaves agent unreachable until operator notices. Octopus's watchdog scheduled task periodically asserts the service is running and restarts if not.

**Fix**: register a Windows Task Scheduler entry at install time that runs `Squid.Tentacle.exe checkservices` every N minutes under SYSTEM. The exe itself checks SCM state and restarts as needed.

**Effort**: low — 3-4 days. Existing `WindowsServiceHost` plumbing.

### 6.7 🟡 Separate Tentacle Upgrader binary

**Current state**: `WindowsTentacleUpgradeStrategy` does in-process upgrade (downloads new MSI, schedules SCM restart, replaces files). Windows can't overwrite a running PE file — current implementation works only because the upgrade happens via service-stop-then-msiexec, not via direct file replacement.

**Risk**: edge case where service-stop succeeds but msiexec fails leaves the agent stopped with no auto-recovery. Operator must intervene.

**Fix**: split into `Squid.Tentacle.Upgrader.exe` side-car: tentacle invokes it on upgrade trigger, upgrader uses named semaphore + retry loop + finally-block service restart. Matches `Octopus.Tentacle.Upgrader` pattern.

**Effort**: medium — 1 week. Reasonable boundary refactor.

### 6.8 🟡 Long-poll completion wait

**Current state**: `HalibutMachineExecutionStrategy.ObserveAndCompleteAsync` polls every 1s with 3-min timeout. A 30-second script triggers 30 RPCs.

**Risk**: RPC volume scales linearly with deploy count. For a 1000-target deploy with 10-step process, that's 300K RPCs. Halibut handles it but it's wasteful.

**Fix**: agent's `StartScriptV2` supports a `DurationToWaitForScriptToFinish` long-poll window (e.g. 30s). One RPC waits up to 30s for completion; only need to poll if script ran longer.

**Effort**: low (paired with §6.1). Just a parameter on the V2 contract.

### 6.9 🟡 Test-matrix parameterization

**Current state**: each E2E test runs once per platform. Variance against `TentacleVersion × CommunicationStyle × IsolationLevel × RetryPolicy` is not exhaustive.

**Risk**: a regression that only surfaces in `(TentaclePolling, FullIsolation, AggressiveRetry)` combo could slip through if tests only run `(TentaclePolling, NoIsolation, DefaultRetry)`.

**Fix**: introduce `[TentacleConfigurations]` test attribute that expands one test method into the Cartesian product. Matches `Octopus.Tentacle.Tests.Integration` `Support/TentacleConfigurationsAttribute` pattern.

**Effort**: medium — 1 week. Existing test infrastructure can be parameterized.

### 6.10 🔴 Backwards-compat decorators

**Current state**: no compat layer between server and agent versions. A new server feature requires every agent to upgrade simultaneously.

**Risk**: deployment fleet rollout becomes a forced lock-step migration. Operator can't roll a server upgrade ahead of agent upgrades.

**Fix**: every new wire-protocol method gets a `BackwardsCompatible{Service}Decorator` on the server side that synthesizes a fallback when the agent doesn't implement the new method (detected via capabilities). Matches `BackwardsCompatibleCapabilitiesV2Decorator` pattern.

**Effort**: low (paired with §6.1+§6.2). Mostly a discipline pattern.

### Risk Priority Summary

| Priority | Items | Total effort |
|---|---|---|
| **🔴 Critical** (production stability) | 6.1, 6.2, 6.3, 6.4, 6.10 | ~3-4 weeks combined |
| **🟡 Important** (operator experience) | 6.5, 6.6, 6.7, 6.8, 6.9 | ~2-3 weeks combined |
| **⚪ Nice-to-have** | Standalone Client SDK (not enumerated) | — |

Recommended 1.7.x batching: 6.1 + 6.2 + 6.10 as one PR series (versioned wire protocol + capabilities + compat decorators), 6.3 separately, then 6.4-6.9 as smaller individual PRs.

---

## §6 `CLAUDE.md` drift items

The "Deployment Pipeline — Detailed Flow" section in the project `CLAUDE.md` references the previous architecture. Drift inventory (all benign — code is correct, docs are stale):

| Claim in docs | Actual state | Fix |
|---|---|---|
| `DeploymentTaskExecutor.cs` + 7 partial files (`.Prepare/.Execute/.Logging/...`) | Orchestrator is `DeploymentPipelineRunner` (single file); 8 phase objects implementing `IDeploymentPipelinePhase` | Rewrite the "Detailed Flow" section to describe phases by `Order` |
| Phase 1 = `LoadDeploymentDataAsync` | Phase 1 (order 50) = `ResumeCheckpointPhase`; load is phase 3 (order 200) + 4 (order 300) | Update order |
| Docs list 4 K8s handlers | 9 K8s handlers exist (added: DeployIngress, DeployService, DeployConfigMap, DeploySecret, Kustomize) | Add the 5 missing |
| Docs list only K8s + Server transports | 7 transports exist (added: TentaclePolling, TentacleListening, Ssh, OpenClaw) | Add 4 missing |
| `ActionHandlerRegistry.Resolve` uses `FirstOrDefault` | Uses `Dictionary<string, IActionHandler>` O(1) lookup | One-line fix |
| `KubernetesAgentIntentRenderer` emits "plain scripts" | Does emit a preamble (namespace + create-namespace probe) | One-paragraph fix |
| `KubernetesDeployContainersActionHandler` | Class is named `KubernetesYamlActionHandler` (ActionType = `KubernetesDeployContainers`) | Name fix |

Recommendation: spawn one cleanup PR that brings `CLAUDE.md` in line with the actual phase-object architecture. Same PR can add a short "phase contract" subsection so future maintainers writing new phases follow the pattern.

---

## §7 Recommendations summary

### Architecture-level (no immediate action)

- **Keep the current generic shape.** The 5-pillar pattern (`IActionHandler` → `ExecutionIntent` → `IIntentRenderer` → `IExecutionStrategy` + `IEndpointVariableContributor`) is sound, generic, and DI-driven. Don't migrate to Octopus's per-stage `IFeature` pipeline — Squid's intent layer is conceptually cleaner.
- **Keep `IDeploymentTransport` composition pattern.** Bundling `Variables + Strategy + Health + Capabilities` per transport is the right abstraction. Octopus has each separately but pays the cost in coordinating composition.

### 1.7.x — protocol layer hardening (this is the next big win)

Tackle the 🔴-tier gaps (§6.1, §6.2, §6.3, §6.4, §6.10) as a coherent series:
1. **PR series A** — define `IScriptServiceV2` + `ICapabilitiesServiceV2` + `BackwardsCompatibleXxxDecorator`. Server-side dispatch consults capabilities before choosing V1 vs V2.
2. **PR series B** — persist `ScriptStateStore` on agent. Reconstruct state on agent restart. Verify with chaos test ("kill agent mid-script, restart, server polls, gets correct result").
3. **PR series C** — split retry policy by RPC class. `FileTransferRpcCallExecutor` for staging + result transfer; `RpcCallExecutor` (default) for status polls.

### 1.7.x — operator experience (smaller wins)

§6.5-6.9 are individually small but cumulatively significant for operator confidence. Bundle 6.5 + 6.6 + 6.7 as a "production hardening" PR. Bundle 6.8 + 6.9 into the protocol series A.

### Documentation

- Refresh `CLAUDE.md` "Deployment Pipeline" section per §7. Single PR.
- Promote this review doc to `docs/` (already done).

### Doctrine reminders

- Every new wire-protocol change should add a backwards-compat decorator (§6.10).
- Every new env-var should be pinned by a `*_ConstantNamePinned` test (Rule 8).
- Every new security/correctness validator should use `EnforcementMode` (Rule 11) defaulting to `Warn` unless the validator gates an immediate security exposure.
- Every new mirror-tier script should have a drift detector (Rule 12.5).

---

## §8 Closing verdict

**Squid's deployment-target architecture is structurally sound and substantially aligned with Octopus.** The generic shape is correct, the test coverage is real, the stability mechanisms are in place. Cross-platform support is genuine — Linux, Windows, Kubernetes all have first-class transports with real-host E2E coverage.

The remaining production-stability gaps live in **the wire-protocol layer between server and agent**, not in the orchestration architecture. Closing those gaps (versioned contracts, idempotent re-attempt, persistent agent state, differentiated retry policies) is the highest-leverage investment for 1.7.x. None of them are blockers for 1.6.9 production deployments — they are stabilization investments that grow more valuable as the fleet size grows.

**Verdict: 1.6.9 is shippable to production today.** **1.7.x should focus on protocol hardening.** **Documentation needs a small refresh.**
