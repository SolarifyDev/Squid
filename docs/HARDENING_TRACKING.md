# Squid Hardening — Findings Tracking

**Generated**: 2026-04-24 from cross-layer audit (5 parallel sub-agents against Squid + OctopusDeploy/OctopusTentacle source).
**Combined with**: 2026-04-23 Tentacle-runtime deep-scan (24 findings, separate table).
**Total live findings**: ~85 (≈20 P0, ≈30 P1, ≈35 P2).

## Legend

| Symbol | Meaning |
|---|---|
| 🔴 **P0** | Security / data corruption / process hang / service outage — fix immediately |
| 🟠 **P1** | Reliability / silent-failure / resource leak — fix before next release |
| 🟡 **P2** | Quality / UX / observability — fix when opportunity |
| 🏔️ **ARCH** | Architectural alignment with Octopus — multi-week effort, plan separately |

## Status column

| Status | Meaning |
|---|---|
| ⬜ | pending — not started |
| 🔄 | in progress — actively being worked on |
| ✅ | fixed — followed by commit SHA |
| 🚫 | won't fix — followed by rationale |
| 🔀 | deferred to architectural-alignment phase |

---

## Section 1 — Crypto & Secrets (🔴 P0-critical)

Everything that touches key material, sensitive variables, or encryption. **These are all pre-release-blocking.**

| ID | Severity | File:Line | Problem | Status | Fix strategy |
|---|---|---|---|---|---|
| **B.1** | 🔴 P0 | `Services/Security/VariableEncryptionService.cs:128-140` + `Api/appsettings.json:26-28` | `MasterKey` defaults to empty string. `Convert.FromBase64String("")` returns `new byte[0]` with no exception. KDF then derives 32-byte key from (empty master, 4-byte-int salt) — fully deterministic for fresh installs. DB dump → offline recovery of every sensitive variable. | ✅ (phase1) | `ValidateMasterKey` helper rejects null/empty, non-base64, < 32 bytes, and all-zero (appsettings default was 32 zero bytes!). `SQUID_ALLOW_INSECURE_MASTER_KEY` opt-in for dev/CI. 12 new tests incl. appsettings.json comment warning + IntegrationTests migrated to random 32-byte key. **BREAKING CHANGE**: server refuses to start without a real MasterKey. |
| **B.2** | 🔴 P0 | `Services/DeploymentExecution/Script/ScriptExecutionHelper.cs:42-46` + agent side `LocalScriptService.cs:607-684` | Per-script `sensitiveVariables.json` encryption password is a `Guid.NewGuid().ToString("N")` **written to `*.key` file next to ciphertext** AND passed via `psi.ArgumentList` as `--password=xxx` (visible in `ps aux` / `/proc/*/cmdline`). Encryption at rest = security theater. | ⬜ | Pass password via stdin pipe or env-var (not argv), and delete `.key` file after Calamari reads it (better: never write it). |
| **B.3** | 🔴 P0 | `Services/Common/SquidVariableEncryption.cs:12-14, 59-62` | PBKDF2 1000 iters + **hardcoded salt `"SquidDep"`** + SHA-1 + AES-128 CBC + no MAC. Vulnerable to bit-flip malleability; would fail any modern crypto review. | ⬜ | Replace with AES-GCM + per-payload random nonce + per-call random 16-byte salt. Migration path: keep legacy decrypt for transition, dual-path encrypt-new. |
| **B.4** | 🟠 P1 | `Services/Security/VariableEncryptionService.cs:41-61` | `DecryptAsync` has no `await` (compiler warning). No dual-key window — key rotation breaks all existing sensitive vars. Errors include internal `variableSetId`. | ⬜ | Remove async, add key-id to envelope (`SQUID_ENCRYPTED_v2:{keyId}:{nonce}:{cipher}`), support dual-key rollover. |
| **B.5** | 🟠 P1 | `Services/Security/VariableEncryptionService.cs:32,54` | `Log.Information("Successfully encrypted variable for VariableSet {id}")` on every call → timing-amplification leak via Seq. | ⬜ | Move to Debug level or remove. |
| **B.6** | 🟠 P1 | `Services/DeploymentExecution/Runtime/Bundles/BashRuntimeBundle.cs:109-117` | `EscapeBashValue` does NOT escape newlines. Variable value containing `\n` breaks out of `export` line → **shell injection** via user-settable non-sensitive variable. | ⬜ | Use `export VAR='%s'` with single-quote wrapping + escape `'` as `'\''`. Or base64-encode values and decode on use. |
| **B.7** | 🟠 P1 | `Services/DeploymentExecution/Script/ServiceMessages/ServiceMessageParser.cs:82-90` | Agent stdout declares `##squid[setVariable ... sensitive='False']` — server trusts the flag blindly. Compromised/buggy script can mark a secret as non-sensitive → leaks to subsequent step logs. | ⬜ | Ignore `sensitive='False'` for values that match server-side sensitive-var patterns; always treat as tainted-until-proven-otherwise for new step. |
| **B.8** | 🟡 P2 | `Observability/SensitiveValueMasker.cs:17` | `MinPatternLength = 4` → 3-char secrets (PINs) silently unmasked. Also `****` could collide with mask token. | ⬜ | Lower floor to 1 with explicit allowlist of "known short strings that aren't secrets"; or hash-and-compare masker. |
| **B.9** | 🟡 P2 | `Services/DeploymentExecution/Script/ServiceMessageParser.cs` + output variables | Qualified `Squid.Action.{Step}.{key}` output variables allow user script to overwrite system-looking names. Unqualified is guarded, qualified is not. | ⬜ | Extend `IsReservedName` to also match qualified forms. |
| **B.10** | 🟡 P2 | `Services/Security/VariableEncryptionService.cs:142-149` | `DeriveKey` salt = 32-bit variableSetId padded with zeros; 10k PBKDF2 iters (below OWASP 600k for SHA-256). | ⬜ | Random 16-byte salt stored in envelope; bump to 600k iters or switch to Argon2id. |
| **B.11** | 🟡 P2 | `Core/VariableSubstitution/Templates/EvaluationContext.cs:176-180` + `TemplateEvaluator.cs:138` | Recursion detection uses exception-message substring `"self referencing loop"` — fragile. Missing variable `#{DoesNotExist}` renders verbatim in output. | ⬜ | Proper typed exception `VariableRecursionException` + fail-closed on missing `#{...}`. |

---

## Section 2 — Authorization & Multi-space (🔴 P0-critical)

Security boundary around tenant/space isolation + API surface.

| ID | Severity | File:Line | Problem | Status | Fix strategy |
|---|---|---|---|---|---|
| **D.1** | 🔴 P0 | `Services/Caching/Redis/RedisCacheService.cs:20, 31` | `JsonConvert.DeserializeObject<T>(..., TypeNameHandling.Auto)` → **RCE via Newtonsoft gadget chains** if Redis is compromised / shared / misconfigured. Fires on every authenticated request (api-key cache). | ✅ (phase1) | `TypeNameHandling.None` + `RedisCacheService.JsonSettings` extracted for pinning; 2 tests (pin + behavioural Dog-ctor-counter). |
| **D.2** | 🔴 P0 | 10 commands: `AssignRoleToTeamCommand`, `UpdateRoleScopeCommand`, `AddTeamMemberCommand`, `RemoveTeamMemberCommand`, `UpdateTeamCommand`, `DeleteTeamCommand`, `CreateUserRoleCommand`, `UpdateUserRoleCommand`, `DeleteUserRoleCommand`, `RemoveRoleFromTeamCommand` | These commands do NOT implement `ISpaceScoped` → `FilterBySpace` returns all system-level roles (including `AdministerSystem`). Any `TeamEdit` holder in any space can assign `AdministerSystem` to their own team → **privilege escalation to full admin**. | ✅ | Added `ISpaceScoped` + writable `int? SpaceId` to all 10 commands (close body-drift). Added explicit anti-escalation guard in `UserRoleService.AssignRoleToTeamAsync`: if the target role contains any `SystemOnly` permission (`AdministerSystem`, `UserRoleEdit`, `SpaceEdit`, …), caller must hold `AdministerSystem` at system level. Fails closed on null `ICurrentUser.Id`. `UpdateTeamCommand.SpaceId` converted `int → int?` with `AutoMapper.Condition` so null values don't reset `Team.SpaceId` to 0. Tests: 20 pinning (ISpaceScoped contract for each of the 10 commands), 5 unit tests for the escalation guard (positive, SystemOnly, mixed, SpaceOnly-negative, null-caller), 3 integration tests for the guard via real DI + DB (negative, positive with real admin caller, SpaceOnly counterpoint). Reverse-verify confirmed. |
| **D.3** | 🔴 P0 | `Services/Machines/Upgrade/UpgradeMachineCommand.cs:65` + `Middlewares/Authorization/AuthorizationSpecification.cs:43-53` + framework-wide | H-19 known, acknowledged in code comments, NOT fixed. `EnsurePermissionAsync` runs BEFORE resource lookup using header-supplied SpaceId. Any `MachineEdit` holder can target a machine in a different Space by setting `X-Space-Id` to a space they have perms in. Applies to **all** `{resource}Id + ISpaceScoped` commands. | ⬜ | Framework-level: load resource first → derive SpaceId from resource → check permission against resource's SpaceId, not header's. Requires auditing every permission-gated command. |
| **D.4** | 🔴 P0 | `Middlewares/SpaceScope/SpaceIdInjectionSpecification.cs:25` + all `Register*/Generate*` commands | Reflection filter is `prop.PropertyType != typeof(int?)` — skips non-nullable `int SpaceId` properties. Body-supplied SpaceId never overridden by header injection → operator can POST `{"SpaceId": <otherSpaceId>}` and write to that space if they have perms there. | ⬜ | Expand filter to also handle non-nullable `int` when property name is `SpaceId`. Integration test covers body vs header drift. |
| **D.5** | 🟠 P1 | `Api/Authentication/ApiKey/ApiKeyAuthenticationHandler.cs:38-42` + `Services/Account/AccountService.cs` | API-key cache is invalidated only on `UpdateUserStatusAsync` and `DeleteApiKeyAsync`. Direct SQL user-disable / any other mutation path → stale cache entry valid for up to 1h. Also no `UserAccount.IsDisabled` check at cache-hit time. | ⬜ | Check `IsDisabled` on cache-hit + reduce cache TTL to 5 min + add invalidation to every `UpdateUser*` entry point. |
| **D.6** | 🟠 P1 | `Services/Identity/ICurrentUser.cs:27,42` + `Services/Identity/CurrentUsers.cs:7` | `InternalUser.Id = 8888` is mutable `static int` — not `const`. If `HttpContext == null` for `ApiUser` (background job scope, DI mishap, test-mode leak), `_currentUser.Id == 8888` → authorization middleware returns immediately, bypassing ALL checks. | ⬜ | Make `const`, reserve 8888 in DB identity sequence, throw on null `HttpContext` (fail-closed). |
| **D.7** | 🟠 P1 | `Api/Extensions/CorsPolicyExtension.cs:10-19` | `AllowAnyHeader + AllowAnyMethod + AllowCredentials + WithOrigins(config-list)`. If an allow-listed origin is compromised (CNAME takeover, subdomain) → CSRF on cookie flow. | ⬜ | Enforce `SameSite=Strict` on auth cookies + add anti-forgery token to state-changing endpoints. Audit allow-list origins. |
| **D.8** | 🟠 P1 | `Services/Machines/MachineDataProvider.cs:60-74` (`GetMachinePagingAsync`) | If `spaceId == null`, space filter is skipped → cross-space machine listing (enumeration of names, endpoints, thumbprints). | ⬜ | Require `spaceId` parameter, reject `null` unless caller has `AdministerSystem`. |
| **D.9** | 🟡 P2 | `Services/Authorization/AuthorizationService.cs:109` (`FilterBySpace`) | `r.SpaceId == null || r.SpaceId == spaceId` — system-level roles always included regardless of space. Amplifies D.2. | ⬜ | Keep as-is for `AdministerSystem` but split other system-level roles from space-scoped ones explicitly. |
| **D.10** | 🟡 P2 | `Services/Caching/ICacheManager.cs:55-81` `GetOrAddAsync` | No locking → classic thundering-herd on cache miss boundary. | ⬜ | `SemaphoreSlim` keyed by cache-key. |
| **D.11** | 🟡 P2 | `Services/Account/AccountService.cs:215` | API keys stored **plaintext** in DB. DB dump → keys reusable forever (no rotation signal). | ⬜ | Hash at rest (bcrypt/argon2), prefix token with key-id for O(1) lookup, store only hash + prefix. Migration path: dual-path read during rollout. |
| **D.12** | 🟡 P2 | Controllers | `GetUpgradeLogRequest` returns agent bash log which can contain tokens / paths / secrets. Ensure sensitive-value masker runs at response serialization, not just at ingest. | ⬜ | Apply masker at controller-response layer for any log-returning endpoint. |

---

## Section 3 — Kubernetes Agent (🔴 P0 + 🟠 P1)

K8s-specific script execution. Three critical security issues.

| ID | Severity | File:Line | Problem | Status | Fix strategy |
|---|---|---|---|---|---|
| **C.1** | 🔴 P0 | `Services/DeploymentExecution/Kubernetes/KubernetesPodManager.PodSpec.cs:64` + `Settings/Kubernetes/KubernetesSettings.cs:8` | Default `ScriptPodImage = "bitnami/kubectl:latest"` + `ImagePullPolicy=IfNotPresent`. Floating `:latest` tag. Registry/tag repoint → arbitrary code execution in the cluster. | ✅ | Introduced `ScriptPodImageValidator.EnsureSafe` — regex `@sha256:[a-fA-F0-9]{64}$` required. Default `ScriptPodImage` changed to empty string (fail-closed). Wired into `BuildPodSpec` AND `ApplyTemplateOverrides` (defence-in-depth). Escape hatch: env var `SQUID_ALLOW_UNPINNED_SCRIPT_POD_IMAGE=1` with pinned-literal test. Malformed `@sha256:` throws always. 21 validator decision-matrix tests + 3 BuildPodSpec wiring tests + 1 template-override wiring test (reverse-verify confirmed). |
| **C.2** | 🔴 P0 | `Services/DeploymentExecution/Kubernetes/HelmUpgradeScriptBuilder.cs:51-56, 110` | Helm repo username/password and `--set` sensitive values are substituted directly into the rendered shell script (via `EscapeBash`). Ends up in `pod.args`, pod logs, script.sh on NFS, kubectl audit. | ⬜ | Write credentials to a per-job K8s Secret; mount as file or project via envFrom; reference via `HELM_*` env or `--values` file path. |
| **C.3** | 🔴 P0 | `Services/DeploymentExecution/Kubernetes/KubernetesAgentHealthCheckStrategy.cs:18-39` | Health check only probes Halibut capabilities — does NOT validate agent can actually create pods (RBAC, PVC mount, SA token). Broken agent reports Healthy until first real deployment. | ⬜ | Add a "describe ScriptPodTemplate" dry-run round-trip at end of health check. |
| **C.4** | 🟠 P1 | `Services/DeploymentExecution/Kubernetes/KubernetesPodManager.PodSpec.cs:384-395` (`BuildPodSecurityContext`) | Default `ScriptPodRunAsNonRoot=false` + no UID → pod runs as **root**. `bitnami/kubectl` is root-by-default. `fsGroup` never set; NFS workspace owned as root. | ⬜ | Default to `runAsNonRoot:true, runAsUser:1000, fsGroup:1000, seccompProfile:RuntimeDefault, readOnlyRootFilesystem:true`. |
| **C.5** | 🟠 P1 | `Services/DeploymentExecution/Kubernetes/KubernetesPodMonitor.cs:109-199` | Orphan cleanup depends on agent leader election being up. If both replicas lose leadership → no reaper. Also pod has no `OwnerReference` to Job, so Kubernetes GC can't help. | ⬜ | Use K8s Job with `ttlSecondsAfterFinished` — cluster GC reclaims even if agent is down. |
| **C.6** | 🟠 P1 | `Services/DeploymentExecution/Kubernetes/KubernetesPodManager.PodSpec.cs:66-69` + `ScriptPodService.cs:131-171` | Workspace NFS PVC is `ReadWriteMany`; script.sh + variables.json written plaintext. Another pod on same PVC can read cross-ticket via workspace mount. | ⬜ | Use per-pod K8s Secret + ConfigMap envFrom projection (RBAC-scoped, not cross-readable on disk). |
| **C.7** | 🟠 P1 | `Services/DeploymentExecution/Kubernetes/KubernetesAgentIntentRenderer.cs:195-205` | `kubectl config set-context --current --namespace="{ns}"` persists globally in pod's `$KUBECONFIG` — user-script background process inherits mutated namespace. | ⬜ | Pass `-n {ns}` explicitly on every kubectl invocation; don't mutate the shared kubeconfig. |
| **C.8** | 🟠 P1 | `Services/DeploymentExecution/Kubernetes/KubernetesPodManager.PodSpec.cs:55` + `KubernetesSettings.cs:9` | `ScriptPodServiceAccount="squid-script-sa"` with no accompanying RBAC shipped. Operator must wire manually; default SA (if missing) is cluster-default (permissive). | ⬜ | Ship chart-managed SA + Role with minimum verbs. Agent startup probes SA existence; fail-fast if missing. |
| **C.9** | 🟡 P2 | `Services/DeploymentExecution/Kubernetes/KubernetesPodManager.cs:33-36` (`_createLocks`) | `SemaphoreSlim` per ticketId never removed → unbounded dict growth on long-running agent. | ⬜ | Remove entry after release, or use size-bounded LRU. |
| **C.10** | 🟡 P2 | `Services/DeploymentExecution/Kubernetes/KubernetesPodManager.cs:202-211` (`CleanupTerminatedPod`) | `ContainerStatuses.FirstOrDefault()` — if `nfs-watchdog` is first, not `script`, `FinishedAt` is wrong. | ⬜ | Filter by container name `"script"` consistently. |
| **C.11** | 🟡 P2 | `Services/DeploymentExecution/Kubernetes/KubernetesAgentEndpointVariableContributor.cs:13-25` | Missing standard K8s context vars (`ClusterUrl`, `Account.*`) that shared helm/apply builders reference. | ⬜ | Mirror full contributor coverage; handler must not depend on vars that only KubernetesApi contributes. |

---

## Section 4 — Deployment Pipeline (🔴 P0 + 🟠 P1)

Server-side orchestrator. Concurrency and cancellation correctness.

| ID | Severity | File:Line | Problem | Status | Fix strategy |
|---|---|---|---|---|---|
| **A.1** | 🔴 P0 | `Services/DeploymentExecution/Completion/DeploymentCompletionHandler.cs:40-45` + `DeploymentPipelineRunner.cs:65-67` | Cancel-vs-Fail race. If user cancel fires AFTER an exception but BEFORE terminal write, task ends `Failed` instead of `Cancelled`. Checkpoint may emit `DeploymentFailedEvent` while UI shows cancel. | ⬜ | Check `CT.IsCancellationRequested` first before deciding terminal state. |
| **A.2** | 🔴 P0 | `Services/DeploymentExecution/Parallel/TargetParallelExecutor.cs:44-52` | `Task.WhenAll` throws only FIRST exception. Subsequent failures observed+discarded silently. | ⬜ | Collect exceptions via `AggregateException` + per-target try/catch; log each. |
| **A.3** | 🔴 P0 | `Services/DeploymentExecution/Phases/ExecuteStepsPhase.cs:85-86` + `.Execute.cs:72-96` + `BatchCheckpointState` | `_batchStates` is non-concurrent `Dictionary<int, BatchCheckpointState>` written from parallel `Task.WhenAll`. `CompletedMachineIds` / `FailedMachineIds` are `HashSet<int>` — not thread-safe. → checkpoint corruption, lost terminal markers on resume. | ⬜ | Convert to `ConcurrentDictionary` + lock-protected HashSets (or use `ImmutableHashSet` with CAS). Unit test spawns parallel Task.WhenAll and asserts final state consistent. |
| **A.4** | 🟠 P1 | `Services/DeploymentExecution/Phases/ExecuteStepsPhase.Execute.cs:72-96` | `catch when (step.IsRequired)` fires `failFastCts.Cancel()` then marks OTHER targets' OCEs as "failed" on checkpoint even though they were aborted, not genuinely failed. Resume skips them as terminal. | ⬜ | Differentiate OCE due to failFastCts from actual exception; mark aborted targets as "pending-resume" not "failed". |
| **A.5** | 🟠 P1 | `Services/DeploymentExecution/Phases/ExecuteStepsPhase.cs:156-171` + output-var merge under lock | `_ctx.Variables.AddRange(result.OutputVariables)` — no de-dup on same variable name from different targets; last-writer-wins ordering is `Task.WhenAll` dependent (non-deterministic). | ⬜ | Explicit conflict resolution: first-writer-wins with log warning on conflict, or machine-id-qualified. |
| **A.6** | 🟠 P1 | `Services/DeploymentExecution/Script/ServiceMessages/ServiceMessageParser.cs:23-39` | Output variable capture `result[name] = variable` — later lines override earlier. User script can `echo '##squid[setVariable name="Squid.Deployment.Id" ...]'` and overwrite system vars. | ⬜ | Reserved-name rejection for `Squid.*` / `System.*` prefixes at parse time. |
| **A.7** | 🟡 P2 | `Services/DeploymentExecution/Infrastructure/DeploymentPipelineRunner.cs:128-129` | `ConcurrencyMaxWait` timeout logs `Warning … proceeding anyway`. Defeats the concurrency tag — two executors CAN run on tagged resource. | ⬜ | Throw after timeout; fail-closed. |
| **A.8** | 🟡 P2 | `Services/DeploymentExecution/Infrastructure/HalibutScriptObserver.cs:241-249` | Log buffer truncation removes OLDEST entries — loses CAUSE of failure on long deploys. | ⬜ | Truncate TAIL (keep head for cause analysis). Or persist incrementally. |
| **A.9** | 🟡 P2 | `Services/DeploymentExecution/Phases/ResumeCheckpointPhase.cs:43-58` | `JsonException` → start fresh + log warning. Silent re-deployment of completed machines on corruption. | ⬜ | Fail closed; require explicit "reset checkpoint" operator action. |
| **A.10** | 🟡 P2 | `RetentionCleanupPhase.cs:13-20` + `DeploymentCompletionHandler.cs:97-118` | Three "log warning and continue" swallows. Masks DB/service outages. | ⬜ | Emit structured Seq events + surface to health-check. |
| **A.11** | 🟡 P2 | `TargetParallelExecutor.cs:20` | No global parallelism semaphore → MaxParallelism=50 concurrent batches exhaust server pool. | ⬜ | Add executor-wide `SemaphoreSlim` sized to `workerCount * 2`. |
| **A.12** | 🟡 P2 | `Services/DeploymentExecution/Phases/PrepareTargetsPhase.cs:69-76` | `catch { return; }` on malformed `EnvironmentIds` JSON → account scope enforcement bypassed. | ⬜ | Hard-fail with detailed error pointing at the malformed account. |

---

## Section 5 — Halibut Communication (🔴 P0 + 🟠 P1)

Server-side Halibut usage. Protocol version parity + cancellation.

| ID | Severity | File:Line | Problem | Status | Fix strategy |
|---|---|---|---|---|---|
| **E.1** | 🔴 P0 | `Services/DeploymentExecution/Infrastructure/HalibutScriptObserver.cs:124` + `IAsyncScriptService.cs` | No per-RPC `CancellationToken` on `GetStatusAsync`/`CompleteScriptAsync`/`CancelScriptAsync`. CT only checked BETWEEN polls. Stuck agent → 5min `TcpReceiveTimeout` before observer can abort. User-cancel doesn't propagate through in-flight RPC. | ⬜ | Adopt Octopus V2 pattern: `IAsyncClientScriptServiceV2` accepts `HalibutProxyRequestOptions { RequestCancellationToken }`. Requires Halibut client update. |
| **E.2** | 🔴 P0 | `Services/Tentacle/IScriptService.cs` + `HalibutMachineExecutionStrategy` | Server speaks only V1. No V2/V3-alpha fallback. Future V2-only tentacle → "NoMatchingService" hard failure. No version negotiation. | 🏔️ | Add `ScriptServiceVersionSelector` that probes capabilities first, picks highest common version. Major contract change. |
| **E.3** | 🟠 P1 | `Services/DeploymentExecution/Infrastructure/HalibutMachineExecutionStrategy.cs:29-71` | Capabilities cache exists but `ExecuteScriptAsync` never reads it — always calls V1 `StartScriptAsync`. | ⬜ | Read capabilities cache before dispatch; map to protocol version. Prerequisite for E.2. |
| **E.4** | 🟠 P1 | `Halibut/PollingTrustDistributor.cs:40-69` | `TrustOnly` REPLACES entire trust list on every registration. Under burst registration → O(N) DB + brief polling-connection drops. Also `.GetAwaiter().GetResult()` in `IStartable.Start` → Autofac-scope deadlock risk. | ⬜ | Switch to additive `Trust(thumbprint)` API. Remove sync-over-async in startup path. |
| **E.5** | 🟠 P1 | `HalibutScriptObserver.cs:188-232` (liveness probe) | Liveness probe opens a SECOND Halibut connection every 5s — 30-min deploy = 360 extra capability RPCs + potential TLS handshakes. Per-probe exception allocation if service missing. | ⬜ | Piggyback liveness on main polling channel (V2 streaming would solve this). Or make probe interval dynamic (exponential backoff when healthy). |
| **E.6** | 🟡 P2 | `HalibutMachineExecutionStrategy.cs:44 + 57-70` | Only `HalibutClientException` + `OperationCanceledException` distinguished. `HalibutNetworkException` lumped with generic. | ⬜ | Add `IsTransientNetworkError` helper; align with LinuxTentacleUpgradeStrategy's pre-dispatch vs mid-dispatch split. |
| **E.7** | 🟡 P2 | `HalibutScriptObserver.cs` observer CT source | Observer CT comes from Hangfire job scope — if user clicks "cancel deployment" and `TaskCancellationRegistry.Cancel(taskId)` is not called, observer polls to `scriptTimeout`. | ⬜ | Ensure every UI cancel path calls the registry. Unit test the registry→observer propagation. |
| **E.8** | 🟡 P2 | `HalibutMachineExecutionStrategy.cs:92-104` | Deterministic `ScriptTicket` = SHA256(taskId, step, action, machineId). Retry on same attempt → same ticket → agent may return OLD ScriptStatusResponse. Idempotency-breaking-semantics lost. | ⬜ | Match Octopus: `new ScriptTicket(Guid.NewGuid())` per attempt. |
| **E.9** | 🟡 P2 | `Halibut/PollingTrustDistributor.cs:40-58` | Initial trust load backgrounded via `Task.Run`. During warmup, pre-existing tentacles TLS-rejected → reconnect churn. | ⬜ | Block startup until trust loaded (fail-fast if DB down). |
| **E.10** | 🟡 P2 | `Halibut/HalibutModule.cs:23` | `HalibutTimeoutsAndLimits.RecommendedValues()` hardcoded. No env override. Violates Rule 8 (env-var escape hatch for air-gapped operators). | ⬜ | Add `HalibutSetting.Timeouts` config section + env fallbacks. Pin env var names in test. |

---

## Section 6 — Tentacle Runtime (🔴 P0 + 🟠 P1)

From **2026-04-23 deep-scan** (24 findings) — cross-verified by this audit round. Most are still open.

| ID | Severity | File:Line | Problem | Status |
|---|---|---|---|---|
| **T.1** | 🔴 P0 | `Tentacle/Certificate/ServerCertificateValidator.cs:63-69` | TLS pin fallback: unpinned certs accepted with warning. MITM door open. No `InsecureAllowUnpinned` opt-in. | ✅ (phase1) | Extracted pure `ValidateCore` + fail-closed by default + `SQUID_ALLOW_UNPINNED_SERVER_CERT` opt-in. 7 new tests (chain valid, thumbprint match/mismatch, no-thumbprint-no-optin rejects, opt-in accepts, env var name pin). **BREAKING CHANGE** for operators who didn't configure ServerCertificate — migration via opt-in env var during rollout. |
| **T.2** | 🔴 P0 | `Tentacle/ScriptExecution/LocalScriptService.cs:217` | `ticketId` path traversal via `../../`. Absolute-path attack inoculated by `squid-tentacle-` prefix; `..` traversal still works. | ✅ (phase1) | Added `TicketIdWhitelist = ^[a-zA-Z0-9_-]{1,64}$` regex + throw-on-mismatch in `ResolveWorkDir`. 20 tests (6 legit + 13 malicious theory + 1 length-cap). |
| **T.3** | 🔴 P0 | `Tentacle/Core/TentacleHalibutHost.cs:167-177` | Sync-over-async adapter: `IAsyncScriptService.StartScriptAsync => Task.FromResult(_inner.StartScript(command))`. Inside `LocalScriptService.cs:72` there's `_isolationMutex.AcquireAsync().GetAwaiter().GetResult()`. Blocks Halibut worker threads. | 🔀 ARCH |
| **T.4** | 🔴 P0 | `Tentacle/Core/TentacleHalibutHost.cs:93` | `_runtime.Poll(pollUri, serverEndpoint, CancellationToken.None)` — no CTS for graceful drain. | ⬜ |
| **T.5** | 🔴 P0 | `Tentacle/ScriptExecution/LocalScriptService.cs:72-95` | mutex handle acquired but not transferred to RunningScript until line 95. Any exception between → mutex stuck forever. | ✅ (phase2) | Wrapped post-acquire setup (dir create, state save, script + additional file writes, process start) in `try { ... } catch { isolationHandle?.Dispose(); throw; }`. Null out after successful `RunningScript` construction so happy-path is a no-op. `LockRelease.Dispose` is idempotent so transient mis-paths are safe. 1 end-to-end regression test: pre-create a directory collision to force `WriteAdditionalFiles` mid-loop throw → assert 2nd FullIsolation script with same mutex name starts within 5s (pre-fix it timed out). Reverse-verify confirmed. Compounds the T.7 fix: rethrow now reliably triggers this path. |
| **T.6** | 🔴 P0 | `Tentacle/Registration/TentacleListeningRegistrar.cs:34-44` | Listening registration: `ServerUrl == "https://localhost:7078"` → fake success return. Fragile literal compare. | ✅ (phase2) | Centralized the sentinel behind `TentacleSettings.DefaultServerUrlSentinel` + `IsAutoRegistrationUnconfigured()` helper. Both call sites (`TentacleListeningRegistrar.RegisterAsync` + `RegisterCommand.ExecuteAsync`) now route through the helper — drift between call sites is impossible. 8 new tests: pin the literal + `TentacleSettings.ServerUrl` default matches sentinel + unconfigured matrix (null/empty/whitespace/tab/sentinel) + positive matrix including `http://localhost:7078` and `https://localhost:9999` (legitimate local-dev URLs that pre-fix were silently dropped). Reverse-verify confirmed. |
| **T.7** | 🔴 P0 | `Tentacle/ScriptExecution/LocalScriptService.cs:610-614` | `WriteAdditionalFiles` catch is empty — doesn't rethrow. Script runs with missing input files. | ✅ (phase2) | Empty `catch { cleanup; }` → `finally { cleanup; }` with implicit rethrow. Temp file still removed in all paths. 3 new tests: target-path-is-directory → throws, first-file-fails → subsequent files NOT written, temp file cleaned up on failure. Reverse-verify confirmed. |
| **T.8** | 🔴 P0 | `Tentacle/Flavors/LinuxTentacle/TentacleListeningRegistrar.cs:61-65` | `ApiKey` sent as plain HTTP header without https-scheme enforcement. | ✅ (phase1) | Added `EnsureSchemeSafeForSecret` helper + `SQUID_ALLOW_HTTP_REGISTER` env-var opt-in escape hatch. 20 tests cover https-safe, http+secret=throw, http+secret+opt-in=warn, http-no-secret=safe, ftp/malformed=reject, + env-var name pinning. |
| **T.9** | 🟠 P1 | `Tentacle/ScriptExecution/LocalScriptService.cs` `IsSameProcessAlive` | PID-recycling deeper check: only StartTime cross-checked, not image path (`proc.MainModule.FileName`). 2s tolerance window could admit fast-recycled PID. | ⬜ |
| **T.10** | 🟠 P1 | `Tentacle/Core/TentacleHalibutHost.cs:140` | `_runtime.Listen(port)` defaults to 0.0.0.0. No `ListenIPAddress` config. | ⬜ |
| **T.11** | 🟠 P1 | `Tentacle/ScriptExecution/LocalScriptService.cs:46,497` | Drain race: `_draining` flag checked at entry, but Halibut has already dispatched. RPC gets exception → server marks task failed (avoidable). | ⬜ |
| **T.12** | 🟠 P1 | `Tentacle/Logging/SequencedLogWriter.cs:77-92` | `DetermineStartSequence` fallback 0 on parse failure → log-file corruption → server sees duplicate seq numbers. | ⬜ |
| **T.13** | 🟠 P1 | `Tentacle/Core/TentacleHalibutHost.cs:161-177` | `AsyncScriptServiceAdapter` receives CT on every method, discards it. Halibut cancellation fully broken. | 🔀 ARCH |
| **T.14** | 🟠 P1 | `Tentacle/Registration/TentaclePollingRegistrar.cs` + `ResolvePollUri:148-153` | Server-returned `subscriptionUri` not validated → malformed URL = startup crash via `UriFormatException`. | ⬜ |
| **T.15** | 🟠 P1 | `Tentacle/Network/PublicHostNameResolver.cs:121-165` | Cloud-metadata calls use `http.Send().Result` and `.Content.ReadAsStringAsync().Result`. Sync-blocking during startup registration → ThreadPool starvation deadlock risk. | ⬜ |
| **T.16** | 🟡 P2 | various | 8 more P2 items (stdbuf, group-read mode, TcpKeepAlive, CleanupOrphanedWorkspaces CreationTime, process-local mutex multi-pod, etc.) | ⬜ |

---

## Section 7 — 🏔️ Architectural Alignment (deferred, multi-week)

| ID | Description | Scope | Estimated effort |
|---|---|---|---|
| **ARCH.1** | **4-phase script protocol** (`Begin/CheckExitCode/CollectLogs/Clean`) with installId correlation token + exit-code file | Matches Octopus TentacleUpgradeMediator approach; solves "agent dead vs still installing" ambiguity | 1-2 weeks |
| **ARCH.2** | **ScriptService V2/V3** with streaming logs + capabilities negotiation | Removes polling overhead for long-running scripts; enables future Kubernetes script service | 2-3 weeks |
| **ARCH.3** | **Tiered timeouts** (15min install / 2min restart / 2min log / 2min clean) | Replaces single 5-min `ScriptTimeout` | 3-5 days |
| **ARCH.4** | **Deployment-conflict guard** — block upgrade when deployment in-flight on same target | Matches Octopus `BlockUpgradeIfDeploymentIsRunning` | 3-5 days |
| **ARCH.5** | **ScriptIsolationLevel.FullIsolation mutex** per machine | Serializes all script dispatches to same machine regardless of source | 2-3 days |
| **ARCH.6** | **K8s Job + ttlSecondsAfterFinished** instead of Pod | Solves orphan-pod cleanup without depending on agent leader election | 3-5 days |
| **ARCH.7** | **Guid-per-attempt ScriptTicket** | Fixes retry idempotency-breaking semantics | 1 day |
| **ARCH.8** | **H-19 framework-level fix**: resource-load-then-permission-check | Touches every `[RequiresPermission]` command | 1 week |
| **ARCH.9** | **IScriptService → fully async** with native CT propagation | Large refactor; solves T.3, T.13, E.1 together | 1-2 weeks |

---

## Execution plan (proposed)

### Phase 1 — P0 Security (Week 1, ~3 days)

Quick-win security fixes, each < 1 day, each a separate PR for clean review:

1. **D.1** — Redis `TypeNameHandling.None` (30 min)
2. **D.2** — Team/role commands `ISpaceScoped` (half day)
3. **B.1** — MasterKey fail-fast on empty (1 hour)
4. **T.1** — TLS pin fallback → opt-in env var (1 hour)
5. **C.1** — K8s default image pin digest (1 hour)
6. **T.2** — ticketId whitelist (30 min)
7. **T.8** — Listening ApiKey https enforce (30 min)

Total: **~1.5 days of coding + 1 day of tests/review = 2-3 days**.

### Phase 2 — P0 Data Integrity (Week 2)

1. **T.5** — StartScript mutex leak protection
2. **T.7** — WriteAdditionalFiles rethrow
3. **T.6** — Listening register localhost:7078 hack removal
4. **A.3** — `_batchStates` thread-safety
5. **A.2** — `Task.WhenAll` exception collection
6. **A.1** — Cancel-vs-Fail race
7. **B.2** — Sensitive vars: remove `.key` file + argv password (requires Calamari side too)

Total: **~4-5 days**.

### Phase 3 — P0 Crypto Modernization (Week 3)

1. **B.3** — AES-GCM + random nonce + random salt (with migration)
2. **B.1** follow-up — key rotation support (B.4)
3. **C.2** — Helm credential via Secret projection

Total: **~1 week**.

### Phase 4 — P0/P1 K8s + Auth hardening (Week 4)

1. **C.3** — K8s health check pod-create dry-run
2. **C.4** — Default non-root pod
3. **D.3** — H-19 framework fix (larger; may spill to week 5)
4. **D.4** — SpaceId injection expansion
5. **D.5/D.6** — API key cache + InternalUser id

Total: **~1-1.5 weeks**.

### Phase 5+ — P1 / P2 / ARCH

Batched by area; sequence per triage discussion after P0 is clean.

---

## Working agreements

- Each fix follows strict **Red-Green-Refactor**:
  1. 思維鏈 — analyze the bug deeply, identify all affected paths
  2. 測試用例 — define failing tests for the bug + edge cases
  3. 確認 — confirm the test is genuinely red against current code
  4. Minimal production code to go green
  5. Reverse-verify — temporarily break the fix, confirm test fails
  6. Full suite pass
- Each PR ≤ 5 files touched, ≤ 500 lines total where possible
- During each fix, **look for related issues** in the touched files — add to this tracking doc if found, don't silently fix-and-forget
- Plan can be adjusted mid-flight if a dependency forces reordering

---

## Open questions

1. **Production impact of B.3 migration** — all existing sensitive-variable ciphertexts need to be readable by the new code path. Dual-path decrypt during transition, then force re-encrypt. Coordination with prod deployment required.
2. **T.3 / T.13 / E.1 interdependence** — these three are all facets of the same sync-over-async → CT-broken problem. Best fixed together as ARCH.9, but each has isolated quick-patch value too.
3. **C.2 Helm Secret projection** — needs agent-side + server-side coordination, and possibly a breaking change for operators who extend `HelmUpgradeScriptBuilder`. Plan rollout window.

---

_Last updated: 2026-04-24. Update as items land._
