# Tentacle Self-Upgrade — Production-Grade Architecture

> **Status:** Phase 1 implemented in `tentacle-self-upgrade` branch.
> **Goal:** One-click in-UI agent upgrade across every target type, atomic +
> safe + observable, with explicit fallback for every failure mode. The
> upgrade itself is allowed to fail; what is **not** allowed is collateral
> damage — a failed upgrade must never break a previously-healthy agent and
> never interrupt an in-flight deployment.

---

## 1 — Why this exists

Currently every Tentacle release forces operators to SSH to every machine and
re-run the install script — same operational pain point Octopus solved a
decade ago. Without a self-upgrade primitive:

- The fleet fragments across versions (`run-the-script-when-someone-remembers`)
- Security patches stall behind manual coordination
- Customer-facing tentacles (B2B agents in client networks) need a support
  ticket per upgrade

We model on Octopus's `TentacleUpgradeMediator` (see local
`/Users/mars/Projects/octopus`), but **fix four known Octopus weaknesses**:

| Octopus weakness | Squid Phase 1 fix |
|---|---|
| Linux upgrade silently dies when neither apt nor yum is on the box ([#8842](https://github.com/OctopusDeploy/Issues/issues/8842)) | Tarball delivery is the **primary** path, not a fallback. Works on alpine / distroless / any glibc 2.31+ Linux. |
| No automatic rollback on failed upgrade — operator left with broken box | Strategy keeps **N-1 binary** at `<install>.bak`; if post-upgrade healthcheck fails, the script atomically swaps back AND verifies the rollback itself worked. |
| Custom signed `Octopus.Upgrader.exe` watchdog must outlive the agent restart on Windows | Use **systemd's own service-restart + `/healthz`** + the bash script itself orchestrates rollback. No new binary to sign / distribute / version-track. |
| Hard-coded "bundled package" version that drifts when someone forgets to bump it | **Auto-detect** version from `AssemblyInformationalVersion` baked in by `dotnet publish -p:Version=$IMAGE_TAG` — drift literally impossible. |

---

## 2 — Architecture diagram

```
                ┌──────────────────────────────────────────────────────┐
                │                   Squid Web UI                        │
                │  Machines list → row → "Upgrade" button (per machine) │
                └─────────────────────────┬────────────────────────────┘
                                          │ POST /api/machines/{id}/upgrade
                                          ▼
            ┌──────────────────────────────────────────────────────────┐
            │  MachineController.UpgradeMachineAsync                    │
            │       └── IMediator → UpgradeMachineCommandHandler        │
            └─────────────────────────┬────────────────────────────────┘
                                      ▼
            ┌──────────────────────────────────────────────────────────┐
            │            IMachineUpgradeService.UpgradeAsync            │
            │  1. Look up Machine + parse CommunicationStyle            │
            │  2. Resolve target version: TargetVersion override OR    │
            │     IBundledTentacleVersionProvider auto-detect          │
            │     (AssemblyInformationalVersion + env var override)    │
            │  3. Resolve current version via                          │
            │     IMachineRuntimeCapabilitiesCache (populated by       │
            │     health checks via Halibut Capabilities probe)        │
            │  4. If current >= target → return AlreadyUpToDate         │
            │  5. ★ ACQUIRE DISTRIBUTED LOCK (RedLock) per machineId ★ │
            │  6. Resolve IMachineUpgradeStrategy by CommunicationStyle │
            │  7. Strategy.UpgradeAsync()                              │
            │  8. ★ ON SUCCESS / INITIATED: invalidate runtime cache  ★ │
            │  9. Return UpgradeResult                                  │
            └─────────────────────┬────────────────────────────────────┘
                ┌─────────────────┴───────────────────────┐
                ▼                                          ▼
   ┌─────────────────────────────┐      ┌──────────────────────────────┐
   │ LinuxTentacleUpgradeStrategy │      │ KubernetesAgentUpgradeStrategy │
   │ (Polling + Listening)        │      │ (Phase 2 placeholder)         │
   │                              │      │                              │
   │ ★ ScriptIsolationLevel.       │      │ Returns NotSupported with     │
   │   FullIsolation ★            │      │ explicit "use helm upgrade"   │
   │ ↳ agent serializes upgrade   │      │ remediation hint until full   │
   │   behind any in-flight       │      │ helm-based implementation     │
   │   deployment script          │      │ (see Phase 2 below)           │
   │                              │      │                              │
   │ Sends upgrade-linux-tentacle │      └──────────────────────────────┘
   │ .sh via Halibut RPC          │
   │ (same channel as deployment  │
   │  scripts — same pipeline,    │
   │  same observers, same logs)  │
   └─────────────┬────────────────┘
                 ▼
        ┌────────────────────────────────────────────────────────┐
        │ upgrade-linux-tentacle.sh (embedded resource)            │
        │  1. Sudo lock file at /var/lib/squid-tentacle/...        │
        │     → idempotent for redelivered messages                │
        │  2. Detect arch via uname → linux-x64 vs linux-arm64    │
        │  3. Pre-flight: 500MB free in /tmp + install dir         │
        │  4. Pre-flight: HEAD probe to GitHub Releases URL        │
        │  5. Download + retry; abort cleanly on failure           │
        │  6. SHA256 verification (when server supplies hash)      │
        │  7. Extract + ldd compat probe (libc mismatch detection) │
        │  8. Stop service → mv current → .bak → mv new → install  │
        │  9. systemctl start + 30s healthcheck loop               │
        │ 10. Verify: running binary --version == target           │
        │ 11. On failure: atomic swap-back AND verify rollback     │
        │ 12. On rollback failure: surface CRITICAL → exit 9       │
        └────────────────────────────────────────────────────────┘
```

---

## 3 — Component reference

| Component | Path | Phase |
|---|---|---|
| `IMachineUpgradeStrategy` interface + `MachineUpgradeOutcome` | `Squid.Core/Services/Machines/Upgrade/IMachineUpgradeStrategy.cs` | 1 |
| `LinuxTentacleUpgradeStrategy` (TentaclePolling + TentacleListening) | `Squid.Core/Services/Machines/Upgrade/LinuxTentacleUpgradeStrategy.cs` | 1 |
| `KubernetesAgentUpgradeStrategy` (placeholder; helm-based in Phase 2) | `Squid.Core/Services/Machines/Upgrade/KubernetesAgentUpgradeStrategy.cs` | 1 stub |
| `WindowsTentacleUpgradeStrategy` | future | 3 |
| `IMachineUpgradeService` orchestrator | `Squid.Core/Services/Machines/Upgrade/MachineUpgradeService.cs` | 1 |
| `IBundledTentacleVersionProvider` (auto-detect via reflection + env override) | `Squid.Core/Services/Machines/Upgrade/BundledTentacleVersionProvider.cs` | 1 |
| `UpgradeMachineCommand` + handler | `Squid.Message/Commands/Machine/`, `Squid.Core/Handlers/CommandHandlers/Machine/` | 1 |
| `MachineController.UpgradeMachineAsync` endpoint | `Squid.Api/Controllers/MachineController.cs` | 1 |
| Embedded upgrade script `upgrade-linux-tentacle.sh` | `Squid.Core/Resources/Upgrade/upgrade-linux-tentacle.sh` | 1 |
| Cache invalidation hook | `IMachineRuntimeCapabilitiesCache.Invalidate(machineId)` | 1 |
| Distributed lock per machineId | `IRedisSafeRunner.ExecuteWithLockAsync` | 1 |
| `MachineNotFoundException` → HTTP 404 | `Squid.Core/Services/Machines/Exceptions/`, `GlobalExceptionFilter` | 1 |
| Tests | `tests/Squid.UnitTests/Services/Machines/Upgrade/` (28 new) | 1 |
| UI button + version column | future (frontend work) | 2 |
| `MachineUpgradeAudit` table | future | 2 |
| Bulk + rolling upgrade controller | future | 2 |
| SHA256 in release pipeline | future | 2 |
| K8s `helm upgrade` strategy | future | 2 |
| Windows tentacle strategy | future | 3 |

---

## 4 — Failure mode matrix (every scenario, every fallback)

This is the section that justifies the "production-grade" claim. Each row is
a real failure scenario, what we do to detect it, and what the operator sees.
**Every row's worst case is "upgrade fails; previous binary keeps running"**
— the existing agent must NEVER end up worse than before the click.

### 4.1 — Server-side failures (before any agent contact)

| Scenario | Detection | Fallback / Behavior | Operator-visible result |
|---|---|---|---|
| `MachineId` doesn't exist | `_machineDataProvider.GetMachinesByIdAsync` returns null | `MachineNotFoundException` → HTTP 404 | Clear 404 with machineId in body |
| Operator forgot `TargetVersion` AND server has no auto-detected version (local dev with empty `AssemblyInformationalVersion`) | `BundledTentacleVersionProvider.GetBundledVersion()` returns empty | Service returns `Failed` with explicit guidance: set `SQUID_BUNDLED_TENTACLE_VERSION` env or build with `-p:Version=<semver>` | UI shows actionable message; nothing dispatched to agent |
| Agent's last health check showed it's **already on the target version** | Compare `_runtimeCache.TryGet(machineId).AgentVersion` to target via `Version.TryParse` semver compare | Return `AlreadyUpToDate`; **strategy not dispatched** (no agent contact at all) | Fast 200 response; "already up to date" UI badge |
| `CommunicationStyle` has no registered strategy (e.g. SSH targets, custom transport) | `_strategies.FirstOrDefault(s => s.CanHandle(style))` returns null | Return `NotSupported` with style name | UI shows "Style X not yet supported for self-upgrade" |
| Two API replicas receive the same upgrade trigger (UI retry, LB failover) | Distributed lock acquisition via `IRedisSafeRunner.ExecuteWithLockAsync` keyed on `squid:upgrade:machine:{id}` | One replica runs; the other returns `Failed` with retry hint | One replica returns success; the other returns "another upgrade in progress, retry"; never double-execution |
| Redis is down (lock infra unavailable) | `RedisSafeRunner` swallows + returns null | Service returns `Failed` with "could not acquire lock; retry" | Operator can retry; never silently runs an unsafe upgrade |
| Cache shows "already up to date" but cache is stale (agent was downgraded out-of-band) | Operator can pass `TargetVersion` explicitly to bypass the cache check (always dispatches strategy) | Operator override path skips the AlreadyUpToDate gate when version differs from cached | Operator has full control |

### 4.2 — Network-level failures (server ↔ agent)

| Scenario | Detection | Fallback / Behavior | Operator-visible result |
|---|---|---|---|
| Agent has been offline for hours; Halibut polling channel is dead | `IHalibutClientFactory.CreateClient` succeeds (lazy), but `StartScriptAsync` blocks until the agent polls | Script timeout (5min) catches eventually; service returns `Failed` with timeout reason | Caller waits up to 5min then sees timeout; agent stays untouched |
| Halibut connection drops mid-upgrade (expected — service restart) | `HalibutClientException` thrown by `ObserveAndCompleteAsync` | Strategy catches and returns `Initiated` — disconnect is normal during agent restart | Operator sees "dispatched, verify via next health check" |
| Network is partitioned and the script never reaches the agent | Halibut polling has its own retry / timeout (default 30s connect) | Caller's `CancellationToken` propagates; service returns `Failed` if timed out | Operator can retry; agent state unchanged (no script ever ran) |

### 4.3 — Agent-side pre-swap failures (script step 1-7, before `mv`)

These are **completely safe** — the script aborts before touching `/opt/squid-tentacle`.

| Scenario | Detection | Fallback / Behavior | Exit code | Operator-visible |
|---|---|---|---|---|
| Architecture not supported (uname returns weird value) | `case "$ARCH"` exhausts | Exit before download | `1` | Server reports "Unsupported architecture: <value>" |
| <500MB free in `/tmp` or install partition | `df -k --output=avail` check upfront | Exit before download | `5` | "Insufficient disk on /tmp staging: X MB free, need 500" |
| Tarball URL returns 404 (typo in version, release not published yet) | `curl -fsSI` HEAD probe | Exit with remediation hint | `6` | "Target tarball not reachable: <url>. Verify release tag exists" |
| Network down during download | `curl -fsSL --retry 3` exhausts retries | Exit before extract | `2` | "Download failed from <url>" |
| Tarball corrupt mid-flight | `tar xzf` reports format error → `set -e` aborts | Exit before swap | `1` (set -e) | Generic shell error in logs |
| SHA256 mismatch (server supplied hash and tarball doesn't match) | `sha256sum | awk` compared to expected | Exit before extract | `7` | "SHA256 mismatch. Expected X, got Y" |
| Extracted archive doesn't contain `Squid.Tentacle` | Existence check post-extract | Exit before swap | `3` | "Extracted archive missing Squid.Tentacle binary" |
| New binary depends on newer glibc than agent has | `ldd | grep "not found"` probe before swap | Exit before swap | `8` | "New binary has unresolved library dependencies (likely glibc mismatch): <ldd output>" |

**In every case above: previous binary keeps running, agent is healthy, no rollback needed.**

### 4.4 — Agent-side post-swap failures (script step 8+)

This is the danger zone — we've stopped the service and moved binaries. Each
failure has an explicit recovery.

| Scenario | Detection | Fallback / Behavior | Exit code | Operator-visible |
|---|---|---|---|---|
| New binary fails to start (segfault, missing config, bad permissions) | systemd `is-active` polling returns non-active for 30s OR `/healthz` never responds | **Atomic rollback**: stop, rm new, mv .bak back, start | `4` | "Upgrade failed: <reason>. Rolling back. Rollback succeeded; agent is healthy on the old binary." |
| New binary starts but reports a different version than expected (e.g. tarball was tampered with mid-pipeline) | Post-restart `--version` check compares to `TARGET_VERSION` | Treat as failure → rollback fires | `4` | "Service is healthy but binary reports version X (expected Y). Rolling back." |
| Rollback ALSO fails (very rare — implies an out-of-band system change like glibc upgrade between original install and now) | Second `is-active` poll after rollback | **Surface loudly** as exit 9; backup left in place at `<install>.bak` (now restored to install) for manual recovery | `9` | "::error:: CRITICAL: rollback also failed. Agent is in an unknown state. Manual intervention required. Backup of pre-upgrade install was at: <path>" |
| Server crashes mid-upgrade | Lock TTL (15min) auto-expires; agent's idempotency lock file ensures a redelivered upgrade is a no-op | Next health check eventually catches the agent's state | Server reports old version OR new (whichever the agent ended on) — operator can re-run | Operator may need to re-trigger if state ambiguous |
| Agent crashes between mv → start (extremely small window) | systemd auto-restart kicks in if unit has `Restart=on-failure`; otherwise install dir = new binary, .bak still present for manual rollback | Best-effort: install dir is in a clean state, just may not be running | systemd should restart, or operator does | Manual restart of squid-tentacle service usually fixes |

### 4.5 — Concurrent operation failures

| Scenario | Detection | Fallback / Behavior | Operator-visible |
|---|---|---|---|
| Operator triggers upgrade while a deployment is mid-flight on this agent | `ScriptIsolationLevel.FullIsolation` on the upgrade script tells the agent's `ScriptIsolationMutex` to wait for any active script | Upgrade serializes behind the deployment; deployment finishes cleanly first; then upgrade proceeds | Slight delay before upgrade actually runs; no interruption to running deployment |
| Operator clicks "Upgrade" twice in 200ms (UI retry) | First click acquires Redis lock; second click waits up to 2s, then returns `Failed` with retry hint | Second click is a no-op as long as the first is in progress | "Another upgrade in progress" toast |
| Operator triggers upgrade on machine A, deployment kicks off on machine A | Halibut script queue serializes; whichever script's StartScriptCommand reaches the agent first runs first | Both eventually run, in arrival order | Both complete |
| Multiple machines upgraded in parallel | Per-machineId lock means N machines = N parallel locks; no contention | All proceed independently | Bulk upgrade in Phase 2 will leverage this |

### 4.6 — Identity / cert preservation failures

The operator's biggest fear: "will my machine come back as a NEW machine after upgrade?"

| Concern | Guarantee | Verification |
|---|---|---|
| Will the machine register itself again as a brand new entry in DB? | **No** — `/opt/squid/certs/tentacle-cert.pfx` lives outside the binary directory and is preserved by `mv` (the mv affects `/opt/squid-tentacle`, certs are at `/opt/squid/certs`) | New binary boots, reads existing cert, sees existing subscription ID, resumes polling on the same Halibut connection. Server sees the same `Machine` row |
| Will polling thumbprint change? | **No** — derived from the cert, and the cert is preserved | Server's Halibut trust list still trusts the same thumbprint |
| Will subscription ID change? | **No** — stored in `/etc/squid-tentacle/instances/*.json`, not touched by the binary swap | Subscription URI in DB unchanged |
| Will any registered API key invalidate? | **No** — keys are server-side objects, not stored on the agent | All N tentacles using the same install API key continue to use it |

---

## 5 — Phase roadmap

### Phase 1 (this PR) — `tentacle-self-upgrade` branch

**In scope:**

- Auto-detected version provider (no .txt file)
- Linux Tentacle (TentaclePolling + TentacleListening) via Halibut bash
- Distributed lock via Redis (multi-replica safe)
- Cache invalidation post-upgrade
- All 8 documented script-side failure exit codes + rollback verification
- HTTP 404 for `MachineNotFoundException`
- 28 unit tests covering every gate

**Out of scope (deferred):**

- UI changes (frontend work)
- `helm upgrade`–based KubernetesAgent strategy
- Windows tentacle
- Audit log table + history endpoint
- Bulk / rolling upgrade
- SHA256 in release pipeline
- Auto-discover "upgrade available" badge on machine list

### Phase 2 — Production hardening + K8s + UI

**In scope:**

- **Kubernetes Agent strategy** via server-side `helm upgrade --reuse-values --set tentacle.image.tag=$VERSION` — no agent-side participation, K8s does the rolling update + automatic rollback. Helm SDK or shell-out.
- **Audit log table** `machine_upgrade_audit` with: machineId, fromVersion, toVersion, status, startedAt, finishedAt, dispatchedBy, detail. Queryable history endpoint `GET /api/machines/{id}/upgrade-history`.
- **Bulk upgrade endpoint** `POST /api/machines/upgrade-batch { machineIds: [...], targetVersion?: string, maxConcurrent?: 5 }` — schedules a parent task, fans out per-machine in parallel up to `maxConcurrent`.
- **Rolling upgrade policy**: `POST /api/environments/{id}/upgrade-roll { batchSize: 3, abortAfterFailures: 1 }` — upgrade N at a time, verify each, halt on threshold.
- **Auto-discover "upgrade available" signal** on every machine list query: `GET /api/machines/list` returns each machine with `{currentVersion, recommendedVersion, upgradeAvailable: bool}` so the UI shows a badge.
- **SHA256 release-pipeline integration**: workflow generates `*.tar.gz.sha256`; `IBundledTentacleVersionProvider.GetExpectedSha256(version, rid)` returns it; bash script enforces.
- **Pre-flight from server side**: dry-run mode `?dryRun=true` returns "what would happen" without touching the agent (current version, target, expected URL, expected SHA, would-be-skipped reason).
- **Air-gapped support**: `BUNDLED_TENTACLE_DOWNLOAD_BASE_URL` env var overrides the default GitHub Releases prefix so private mirrors / S3 buckets work.

### Phase 3 — Windows + cross-platform parity

- **Windows tentacle strategy**: PowerShell mirror of bash flow. `Stop-Service` / `.zip` extract / `Start-Service` + `Test-NetConnection` healthcheck loop + rollback.
- **macOS tentacle** (low priority): launchd-based version of the Linux flow.
- **Auto-upgrade on schedule**: cron-driven background task that auto-upgrades the fleet when the bundled version is N+1 vs current — opt-in per environment / per machine policy.

---

## 6 — Comparison: Squid Phase 1 vs Octopus

| Dimension | Octopus | Squid Phase 1 |
|---|---|---|
| Linux upgrade primary path | apt / yum (fallback to bundled package, [#8842](https://github.com/OctopusDeploy/Issues/issues/8842) tarball broken) | **Tarball download** from GitHub Releases (works on alpine/distroless) |
| Linux rollback | None — operator manual | **Auto via shell** (`mv .bak back` + verify) with explicit exit 9 escalation when rollback also fails |
| Companion process | Custom `Octopus.Upgrader.exe` (signed, distributed, version-tracked separately) | **None** — bash + systemd primitives |
| Upgrade RPC contract | 5 dedicated PowerShell scripts (`TentacleUpgradeBegin/CheckExitCode/CollectLogs/Clean`) | **Same `IAsyncScriptService` Halibut channel** the deployment pipeline already uses |
| Strategy multipolymorphism | `target.Upgrade()` — type-switched per target subclass | **`IMachineUpgradeStrategy` per CommunicationStyle** — symmetric with `IExecutionStrategy` and `IHealthCheckStrategy` |
| Idempotency | `Test-Path Upgrade\{InstallId}` directory check | `flock`-style sentinel at `/var/lib/squid-tentacle/upgrade-<v>.lock` |
| Bundled version source | `IBundledPackageStore` reads embedded .nupkg | **`AssemblyInformationalVersion`** baked in at `dotnet publish -p:Version=$IMAGE_TAG` — single source of truth |
| Multi-replica server safety | None documented | **RedLock per machineId** prevents double-process |
| Cache invalidation post-upgrade | None — relies on next scheduled health check | **Explicit `IMachineRuntimeCapabilitiesCache.Invalidate`** on success/initiated |
| Active deployment guard | None explicit | **`ScriptIsolationLevel.FullIsolation`** — agent's mutex serializes upgrade behind any in-flight deployment |
| Pre-flight checks | Calamari version check only | Disk space + URL HEAD + SHA256 + ldd compat + post-restart version verify |
| Failure observability | Halibut request log | **Same** + per-script-exit-code remediation hint baked into stderr |

---

## 7 — Operator API reference

### Trigger upgrade

```
POST /api/machines/{machineId}/upgrade
Content-Type: application/json
X-API-KEY: <key with MachineEdit permission>

{
  "targetVersion": "1.4.2"   // optional; omit to use auto-detected bundled version
}
```

Response (HTTP 200, body code reflects business outcome):

```json
{
  "data": {
    "machineId": 17,
    "machineName": "mars-mac-docker",
    "currentVersion": "1.3.3",
    "targetVersion": "1.4.0",
    "status": 0,        // see MachineUpgradeStatus enum below
    "detail": "Upgrade to 1.4.0 reported success in 32 log lines"
  },
  "msg": "Success",
  "code": 200
}
```

### `MachineUpgradeStatus` enum

| Value | Name | Meaning |
|---|---|---|
| `0` | `Upgraded` | Agent confirmed running target version |
| `1` | `AlreadyUpToDate` | Pre-skipped; no agent contact made |
| `2` | `NotSupported` | No strategy registered for this CommunicationStyle |
| `3` | `Failed` | Upgrade attempted but did not succeed; agent is on previous version (rollback succeeded) OR in unknown state (rollback failed; check `Detail`) |
| `4` | `Initiated` | Upgrade dispatched; Halibut disconnected mid-script as expected during restart. Verify via next health check. |

### Override version per call

```bash
curl -X POST https://squid-api/.../api/machines/17/upgrade \
  -H "X-API-KEY: $KEY" \
  -d '{"targetVersion":"1.5.0-rc.1"}'
```

### Operator escape hatches

- **Different bundled version per server replica** (e.g. canary): set `SQUID_BUNDLED_TENTACLE_VERSION` env on that pod
- **Air-gapped / forked tarball**: Phase 2 will add `BUNDLED_TENTACLE_DOWNLOAD_BASE_URL`. Phase 1 workaround: set `targetVersion` to your fork tag and pre-mirror the GitHub URL pattern to your registry
- **Forced re-upgrade** even when AlreadyUpToDate: pass `targetVersion` explicitly to a different value, then back

---

## 8 — Test coverage

| Test | Layer | What it locks down |
|---|---|---|
| `BundledTentacleVersionProviderTests.GetBundledVersion_*` | Unit | Auto-detection returns valid semver; BOM-less; no `+sha` suffix; ≤3 segments |
| `BundledTentacleVersionProviderTests.GetDownloadUrl_*` | Unit | URL construction for x64/arm64/pre-release versions; rejects blank inputs |
| `BundledTentacleVersionProviderTests.OverrideEnvVar_ConstantNamePinned` | Unit | Renaming env var would break operator contract; pinned in test |
| `MachineUpgradeServiceTests.MachineNotFound_*` | Unit | 404 path |
| `MachineUpgradeServiceTests.NoBundleAndNoExplicitTarget_*` | Unit | Failed with explicit env-var guidance |
| `MachineUpgradeServiceTests.AlreadyOnTargetVersion_*` | Unit | Pre-skip, strategy never dispatched |
| `MachineUpgradeServiceTests.CurrentVersionNewerThanTarget_*` | Unit | Pre-skip on downgrade attempt |
| `MachineUpgradeServiceTests.OperatorOverridesTargetVersion_*` | Unit | Override beats bundled |
| `MachineUpgradeServiceTests.DispatchesByCommunicationStyle_*` × 2 | Unit | Linux vs K8s strategy resolution |
| `MachineUpgradeServiceTests.NoStrategyForStyle_*` | Unit | NotSupported with style name |
| `MachineUpgradeServiceTests.CacheMiss_ProceedsWithEmptyCurrentVersion` | Unit | Cold cache doesn't block dispatch |
| `MachineUpgradeServiceTests.OnSuccessOrInitiated_InvalidatesRuntimeCache` × 2 | Unit | **Cache invalidation hook** verified for Upgraded + Initiated |
| `MachineUpgradeServiceTests.OnFailureOrNotSupported_LeavesRuntimeCacheIntact` × 2 | Unit | No needless invalidation when nothing changed |
| `MachineUpgradeServiceTests.LockAcquisitionFails_*` | Unit | **Multi-replica safety**: strategy not invoked when lock unavailable |
| `MachineUpgradeServiceTests.LockKeyIsPerMachineId_AllowsParallelDifferentMachines` | Unit | Lock key embeds machineId; namespace prefix stable |
| `KubernetesAgentUpgradeStrategyTests.*` | Unit | Phase 2 placeholder returns NotSupported with helm hint |

**Total: 28 unit tests** in `tests/Squid.UnitTests/Services/Machines/Upgrade/`. All green.

Phase 2 will add: integration tests against a real fake-systemd Linux container,
end-to-end test that runs the actual bash script in a sandbox.

---

## 9 — Migration notes

### For server operators

After deploying Squid Server with this change:

1. **Verify the auto-detected version** matches the desired Tentacle version:
   ```bash
   kubectl logs deploy/squid-api | grep -i "bundled tentacle"   # warning prints if empty
   curl -s https://your-squid/api/server-configuration  # if endpoint exposes it
   ```
2. If the version is empty (local builds, missing GitVersion), set:
   ```yaml
   env:
     - name: SQUID_BUNDLED_TENTACLE_VERSION
       value: "1.4.0"
   ```
3. **Test against one machine first** — pick a non-critical agent, click Upgrade, verify it comes back healthy. Check Seq for any errors.

### For tentacle operators (no action needed)

- Existing Tentacles do not need to be re-registered, certs are preserved
- Existing API keys continue to work
- Subscription IDs unchanged
- Polling thumbprint unchanged

### For UI / frontend (Phase 2 work)

When you add the Upgrade button:

1. Call `POST /api/machines/{id}/upgrade`
2. Show toast based on `data.status`:
   - `0` Upgraded → green "Upgrade successful (1.3.3 → 1.4.0)"
   - `1` AlreadyUpToDate → grey "Already on 1.4.0"
   - `2` NotSupported → grey "Not yet supported for this target type" + show `data.detail`
   - `3` Failed → red "Upgrade failed" + show `data.detail`
   - `4` Initiated → yellow "Upgrade dispatched, verify shortly" + auto-trigger health check after 30s
3. After Initiated/Upgraded, refresh the machine row from `/api/machines/list` to show the new version

---

## 10 — Five-line summary

1. **Per-style strategies** mirror existing `IExecutionStrategy` / `IHealthCheckStrategy` patterns — one new style = one new file.
2. **Auto-detected version** from server's own `AssemblyInformationalVersion` makes "what version should every agent be on" a single source of truth that can't drift.
3. **Bash script over Halibut RPC** reuses every dollar of investment in the existing deployment pipeline (resilience, log streaming, isolation, mute).
4. **Defense in depth**: distributed lock + cache invalidation + Halibut FullIsolation + 8 documented script exit codes + atomic swap with verified rollback + post-restart version sanity check.
5. **Octopus-aligned where Octopus is right; Octopus-improved where Octopus is documented broken** ([#8842](https://github.com/OctopusDeploy/Issues/issues/8842) Linux fallback dies; no rollback) — none of those failure modes survive into Squid.
