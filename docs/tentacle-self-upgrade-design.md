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
| "Bundled package" version coupled to server release — server release N pins agents to N, even if Tentacle ships a security hotfix to N+0.1 in between | **`ITentacleVersionRegistry` queries Docker Hub directly** for the latest published Tentacle, with a 10-minute in-process cache + per-style env var override. Server's own version is irrelevant — Tentacle and Server release on independent cadences if they need to. |

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
            │     ITentacleVersionRegistry.GetLatestVersionAsync(style)│
            │     → live Docker Hub query, 10-min cache, env override  │
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
| `ITentacleVersionRegistry` — single-responsibility version lookup; live Docker Hub query per-style with TTL cache + env override. **No platform-specific methods** (URL pattern, archive format, etc. live on each strategy per ISP). | `Squid.Core/Services/Machines/Upgrade/TentacleVersionRegistry.cs` | 1 |
| Linux tarball URL pattern + air-gap mirror env override (`SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL`) | `LinuxTentacleUpgradeStrategy.BuildDownloadUrl` (private — strategy owns its delivery) | 1 |
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
| Operator forgot `TargetVersion` AND Docker Hub query fails AND no cache AND no env override | `ITentacleVersionRegistry.GetLatestVersionAsync` returns empty | Service returns `Failed` with explicit guidance: set `SQUID_TARGET_LINUX_TENTACLE_VERSION` / `SQUID_TARGET_K8S_AGENT_VERSION` env, or pass `TargetVersion` explicitly | UI shows actionable message; nothing dispatched to agent |
| Docker Hub is down at request time (transient blip) | Live query throws → catch → check stale cache | Service returns last-known-good cached version (better stale answer than refusal) | Upgrade proceeds with most recent successfully-fetched version |
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
- **SHA256 release-pipeline integration**: GitHub release workflow generates `*.tar.gz.sha256` per RID; `LinuxTentacleUpgradeStrategy` fetches it alongside the version (via `ITentacleVersionRegistry` augmented with `GetExpectedSha256(version, rid)` — kept on the strategy so the registry interface stays single-responsibility); bash script enforces — `EXPECTED_SHA256` placeholder is non-empty in Phase 2.
- **Pre-flight from server side**: dry-run mode `?dryRun=true` returns "what would happen" without touching the agent (current version, target, expected URL, expected SHA, would-be-skipped reason).
- **Air-gapped support** (already in Phase 1 for Linux): `SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL` env var overrides the default GitHub Releases prefix so private mirrors / S3 buckets work — the file naming convention `{base}/{version}/squid-tentacle-{version}-{rid}.tar.gz` stays canonical so mirrors just rsync the GitHub release tree. K8s + Windows mirror overrides come in their own strategies.

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
| Bundled version source | `IBundledPackageStore` reads embedded .nupkg, version coupled to server release | **Live Docker Hub query** for `squidcd/squid-tentacle-linux` / `squidcd/squid-tentacle` — Tentacle version is independent of Server version, with 10-min cache + env-var override |
| Multi-replica server safety | None documented | **RedLock per machineId** prevents double-process |
| Cache invalidation post-upgrade | None — relies on next scheduled health check | **Explicit `IMachineRuntimeCapabilitiesCache.Invalidate`** on success/initiated |
| Active deployment guard | None explicit | **`ScriptIsolationLevel.FullIsolation`** — agent's mutex serializes upgrade behind any in-flight deployment |
| Pre-flight checks | Calamari version check only | Disk space + URL HEAD + SHA256 + ldd compat + post-restart version verify |
| Failure observability | Halibut request log | **Same** + per-script-exit-code remediation hint baked into stderr |

---

## 6.5 — Operator setup prerequisites

Three out-of-band things must be in place before clicking Upgrade in the UI
returns anything but `Failed`. None of them are enforced by the server (they
live on the agent host or the network), so the design doc is the authoritative
checklist. Run through this once per environment.

### A. Linux Tentacle: passwordless sudo for upgrade-required commands

The bash upgrade script runs through Halibut RPC under the agent's process
identity. It needs `sudo` for the binary swap (`mv` between root-owned dirs),
service control (`systemctl stop/start`), file ownership (`chown`), and a few
file-system primitives. If sudo prompts for a password the script hangs at the
first sudo call until the Halibut script timeout (5 min), then comes back as
`Failed` with `(no log lines)`.

**Configuration**: drop `/etc/sudoers.d/squid-tentacle-upgrade` on every
Linux Tentacle host:

```sudoers
# /etc/sudoers.d/squid-tentacle-upgrade  (owner: root:root, mode: 0440)
# squid-tentacle (the agent's service user) needs passwordless sudo for the
# binary-swap commands the upgrade script issues. Scoped to the specific
# binaries the script actually invokes — not blanket ALL — so an exploit of
# the agent process can't pivot to an arbitrary root command.
squid-tentacle ALL=(root) NOPASSWD: \
    /bin/systemctl stop squid-tentacle, \
    /bin/systemctl start squid-tentacle, \
    /bin/systemctl is-active squid-tentacle, \
    /bin/mkdir -p /var/lib/squid-tentacle, \
    /usr/bin/touch /var/lib/squid-tentacle/upgrade-*.lock, \
    /bin/rm -f /var/lib/squid-tentacle/upgrade-*.lock, \
    /bin/mv /opt/squid-tentacle*, \
    /bin/rm -rf /opt/squid-tentacle*, \
    /bin/chmod +x /opt/squid-tentacle/*, \
    /bin/chown -R squid-tentacle\:squid-tentacle /opt/squid-tentacle, \
    /bin/ln -sf /opt/squid-tentacle/* /opt/squid-tentacle/squid-tentacle, \
    /bin/ln -sf /opt/squid-tentacle/squid-tentacle /usr/local/bin/squid-tentacle
```

After dropping the file:
```bash
sudo chmod 0440 /etc/sudoers.d/squid-tentacle-upgrade
sudo visudo -c   # validates syntax — MUST report "parsed OK" before continuing
```

**Permissive alternative** (development / single-tenant boxes only):
```sudoers
squid-tentacle ALL=(ALL) NOPASSWD: ALL
```

The Tentacle install script will land sudoers configuration automatically in
Phase 2; until then it's a manual step.

### B. Server → Docker Hub reachability + rate limits

The version registry queries `https://hub.docker.com/v2/repositories/...` with
no auth. Docker Hub anonymous rate limit: **100 requests / 6h / source IP**.

A fleet of N server replicas with M machines doing concurrent upgrades is
bounded to a few queries per 10-minute window thanks to the in-process TTL
cache + in-flight dedupe. But a misconfigured cache (per-pod local) plus high
churn could trip the limit.

**Symptoms of rate-limit hit**: registry's stale-cache fallback kicks in
(check Seq for `[Upgrade] Docker Hub query for ... failed; falling back to
stale cached version`). Operator-visible result: stale version pinned for a
while, then `Failed` if cache is also empty.

**Mitigations**:
1. **Pin a version per replica** via env override — bypasses Docker Hub
   entirely for the override path:
   ```yaml
   env:
     - name: SQUID_TARGET_LINUX_TENTACLE_VERSION
       value: "1.4.0"
     - name: SQUID_TARGET_K8S_AGENT_VERSION
       value: "1.4.0"
   ```
2. **Mirror to private registry** + override delivery URL (covers air-gap too):
   ```yaml
   env:
     - name: SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL
       value: "https://mirror.acme.internal/squid"
   ```
3. (Future) Authenticated Docker Hub access — needs a `squidcd` machine
   account; not implemented in Phase 1.

### C. Agent → release tarball reachability

The bash script downloads `{base}/{version}/squid-tentacle-{version}-{rid}.tar.gz`.
Default base is `https://github.com/SolarifyDev/Squid/releases/download`.
Air-gapped sites set `SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL` (server
side — gets baked into the script template before dispatch) to a mirror
prefix that hosts the same `{version}/{rid}.tar.gz` layout.

The script does a `curl -fsSI --max-time 10` HEAD probe BEFORE touching the
live install — so a 404 (e.g. release tag unpublished) or network error
manifests as exit code 6 with a clear message, not a half-broken install.

### D. Agent local healthcheck endpoint

The bash script polls `http://127.0.0.1:8080/healthz` after `systemctl start`
to confirm the new binary came up. If your Tentacle build exposes the
healthcheck on a different port/path, override via env on the server:

```yaml
env:
  - name: SQUID_TARGET_LINUX_TENTACLE_HEALTHCHECK_URL
    value: "http://127.0.0.1:9090/api/health"
```

(See `LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar` — defaults to the
above when unset.)

### E. Quick verification ladder

Before declaring the environment ready, run this from the server pod:

```bash
# 1. Server can reach Docker Hub (or your override is set)
echo $SQUID_TARGET_LINUX_TENTACLE_VERSION  # if non-empty, skip step 2
curl -sI https://hub.docker.com/v2/repositories/squidcd/squid-tentacle-linux/tags/?page_size=1 | head -1
#   Expected: HTTP/2 200

# 2. Pick one Linux Tentacle target and verify reachability from server
kubectl exec -it deploy/squid-api -- curl -fsI https://github.com/SolarifyDev/Squid/releases/download/1.4.0/squid-tentacle-1.4.0-linux-x64.tar.gz | head -1
#   Expected: HTTP/2 200

# 3. Trigger a dry-style upgrade for one machine and inspect the log
curl -X POST https://your-squid/api/machines/<id>/upgrade -d '{}' -H 'Content-Type: application/json'
#   Successful response: { ..., "status": "Upgraded", ... } within ~60s
#   Failed dispatch: status="Failed", detail starts with "Upgrade dispatch failed before…" → fix sudoers / agent up
#   Failed mid-script: status="Failed", detail like "exit 6: Target tarball not reachable" → fix C
```

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

- **Different target version per server replica** (e.g. canary): set `SQUID_TARGET_LINUX_TENTACLE_VERSION` / `SQUID_TARGET_K8S_AGENT_VERSION` env on that pod
- **Air-gapped / forked tarball** (Phase 1): set `SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL` to your mirror prefix; mirror just rsyncs the GitHub release tree
- **Forced re-upgrade** even when AlreadyUpToDate: pass `targetVersion` explicitly to a different value, then back

---

## 8 — Test coverage

| Test | Layer | What it locks down |
|---|---|---|
| `TentacleVersionRegistryTests.OverrideEnvVar_LinuxConstantNamePinned` | Unit | Renaming `SQUID_TARGET_LINUX_TENTACLE_VERSION` would break canary/air-gap contract |
| `TentacleVersionRegistryTests.OverrideEnvVar_K8sConstantNamePinned` | Unit | Same for `SQUID_TARGET_K8S_AGENT_VERSION` |
| `TentacleVersionRegistryTests.GetLatestVersionAsync_LinuxStyleWithEnvOverride_*` × 2 | Unit | Override short-circuits BEFORE any HTTP IO (proves no captive-dep regression) |
| `TentacleVersionRegistryTests.GetLatestVersionAsync_K8sStyleWithEnvOverride_*` | Unit | K8s routing distinct from Linux |
| `TentacleVersionRegistryTests.GetLatestVersionAsync_OverrideTrimmed_*` | Unit | Whitespace stripped from operator-supplied env value |
| `TentacleVersionRegistryTests.GetLatestVersionAsync_UnknownStyle_*` | Unit | SSH/future styles → empty (graceful) |
| `TentacleVersionRegistryTests.Lifetime_RegistryIsScoped_NotSingleton` | Unit | Pins lifetime to prevent the captive-dep regression on `ISquidHttpClientFactory` (scoped) |
| `LinuxTentacleUpgradeStrategyTests.DownloadBaseUrlEnvVar_ConstantNamePinned` | Unit | Renaming `SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL` would break every mirror operator |
| `LinuxTentacleUpgradeStrategyTests.BuildDownloadUrl_DefaultsToGitHubReleasesPath` × 3 | Unit | x64/arm64/pre-release URL pattern stable |
| `LinuxTentacleUpgradeStrategyTests.BuildDownloadUrl_EnvOverride_*` × 2 | Unit | Mirror retarget for x64 + arm64 |
| `LinuxTentacleUpgradeStrategyTests.ResolveDownloadBaseUrl_StripsTrailingSlash_*` | Unit | Mirror url with `/` doesn't double-slash |
| `LinuxTentacleUpgradeStrategyTests.ResolveDownloadBaseUrl_BlankOverride_*` | Unit | `"   "` falls back to GitHub default |
| `LinuxTentacleUpgradeStrategyTests.CanHandle_OnlyMatchesLinuxTentacleStyles` × 7 | Unit | Polling+Listening only; KubernetesAgent/Api/Ssh/empty/null all rejected |
| `MachineUpgradeServiceTests.UpgradeAsync_MachineNotFound_*` | Unit | 404 path via MachineNotFoundException |
| `MachineUpgradeServiceTests.UpgradeAsync_RegistryReturnsEmpty_*` | Unit | Failed + actionable env-var guidance |
| `MachineUpgradeServiceTests.UpgradeAsync_AlreadyOnTargetVersion_*` | Unit | Pre-skip, strategy never dispatched |
| `MachineUpgradeServiceTests.UpgradeAsync_CurrentVersionNewerThanTarget_*` | Unit | Pre-skip on downgrade attempt |
| `MachineUpgradeServiceTests.UpgradeAsync_OperatorOverridesTargetVersion_*` | Unit | Body override beats auto-resolved version |
| `MachineUpgradeServiceTests.UpgradeAsync_DispatchesByCommunicationStyle_*` × 2 | Unit | Linux vs K8s strategy resolution |
| `MachineUpgradeServiceTests.UpgradeAsync_NoStrategyForStyle_*` × 3 | Unit | NotSupported with style name; doesn't hit registry; **doesn't blame missing version (audit H-1 regression guard)** |
| `MachineUpgradeServiceTests.UpgradeAsync_CacheMiss_*` | Unit | Cold cache doesn't block dispatch |
| `MachineUpgradeServiceTests.UpgradeAsync_OnSuccessOrInitiated_InvalidatesRuntimeCache` × 2 | Unit | **Cache invalidation hook** verified for Upgraded + Initiated |
| `MachineUpgradeServiceTests.UpgradeAsync_OnFailureOrNotSupported_LeavesRuntimeCacheIntact` × 2 | Unit | No needless invalidation when nothing changed |
| `MachineUpgradeServiceTests.UpgradeAsync_LockAcquisitionFails_*` | Unit | **Multi-replica safety**: strategy not invoked when lock unavailable |
| `MachineUpgradeServiceTests.UpgradeAsync_LockKeyIsPerMachineId_*` | Unit | Lock key embeds machineId; namespace prefix stable |
| `KubernetesAgentUpgradeStrategyTests.CanHandle_*` × 7 | Unit | Only KubernetesAgent style accepted |
| `KubernetesAgentUpgradeStrategyTests.UpgradeAsync_*` | Unit | Phase 2 placeholder returns NotSupported with helm hint |

**Phase 1 totals**: 182 unit tests in `tests/Squid.UnitTests/Services/Machines/Upgrade/` across 6 test classes. All green against 4240-test full suite.

### Hardenings landed across TWO audit cycles

**First cycle (commit `X`): 10 holes fixed**
- H-1 strategy-first ordering; H-2 captive-dep lifetime; H-3/H-4/H-5/H-17 strict semver gate; H-8 stale-ref cleanup; H-10/H-11 dispatch + BuildScript tests; H-13 POSIX df.

**Second cycle (this doc update): 14 more holes fixed**

| Hole | What landed |
|---|---|
| **N-1** | Distinguish pre-dispatch (`Failed`) vs mid-script (`Initiated`) `HalibutClientException` — `when (dispatchAcked)` pattern |
| **N-2** | Spec §11 pre-release precedence — `beta.11 > beta.2` via identifier-by-identifier numeric/alphanumeric compare; `IEquatable<SemVer>` + `==/!=` operators |
| **N-3** | Docker Hub pagination — follow `next` link, cap at 10 pages |
| **N-4** | Concurrent fan-out dedupe — `Lazy<Task<>>` + `ConcurrentDictionary.GetOrAdd`; `Task.WaitAsync(ct)` isolates caller cancellation from inner task |
| **N-6** | Outcome-driven cache invalidation — `AgentVersionMayHaveChanged` flag on `MachineUpgradeOutcome`; orchestrator no longer inspects `Status` enum |
| **N-7** | Operator setup docs section §6.5 — sudoers scaffold, Docker Hub rate-limit guidance, agent reachability, healthcheck URL override |
| **N-8** | Bash flock — kernel-level advisory lock, SIGKILL-safe, lock file in `/tmp` (no sudo gymnastics) |
| **H-6** | Drop ticket ID `[..32]` truncation — full GUID entropy; `PreviewStartScriptCommand` test seam |
| **H-7** | `timeout 30` / `timeout 60` around `systemctl stop` / `start` |
| **H-12** | Consolidated single `cleanup()` function + single `trap cleanup EXIT` |
| **H-14** | `{{HEALTHCHECK_URL}}` placeholder + `SQUID_TARGET_LINUX_TENTACLE_HEALTHCHECK_URL` env override |
| **H-15** | `LockExpiry` raised to 20min + **invariant test** pinning `LockExpiry > 2× strategy timeout + 5min buffer` — future strategies with longer timeouts trip this assertion |
| **H-16** | URL routing test via fake `HttpMessageHandler` + poisoned tags on wrong repo |
| **H-19** | Partial mitigation (controller nulls body `SpaceId`); documented framework-level fix is out of scope |

### Additional defensive improvements

- **Malformed endpoint JSON** → `Failed` with actionable detail (was `NotSupported ''`)
- **Strategy uniqueness check** — two strategies claiming same style now throw at first dispatch, not silently mis-route
- **Bash integration tests** (21 cases) — `bash -n` syntax guardrail + invariant grep for every exit code, every flow anchor. Phase 2 docker-based fake-systemd suite covers end-to-end execution.

Phase 2 will add:
- Integration test against a fake-systemd Linux container running the actual bash script end-to-end (covers atomic swap + rollback + healthcheck loop)
- E2E test booting `TentacleStub`, dispatching upgrade, asserting script payload received over real Halibut polling and replied with mock success
- SHA256 release-pipeline integration
- K8s Agent helm-based upgrade (strategy stub replaced)
- Bulk upgrade UI + endpoint
- Framework-level authorization fix for H-19 (permission check AFTER resource lookup)

---

## 9 — Migration notes

### For server operators

After deploying Squid Server with this change:

1. **Verify auto-resolution works** by triggering a single upgrade with no body and checking the response's `targetVersion`:
   ```bash
   curl -X POST https://your-squid/api/machines/<id>/upgrade -d '{}' -H 'Content-Type: application/json'
   #   { ..., "targetVersion": "1.4.2", ... }       ← live Docker Hub query worked
   #   { ..., "status": "Failed", "detail": "...set SQUID_TARGET_LINUX_TENTACLE_VERSION..." }   ← Docker Hub unreachable, set env override
   ```
2. If Docker Hub is unreachable from your server (firewall, air-gap), pin a version explicitly:
   ```yaml
   env:
     - name: SQUID_TARGET_LINUX_TENTACLE_VERSION   # for Linux Tentacles (Polling + Listening)
       value: "1.4.0"
     - name: SQUID_TARGET_K8S_AGENT_VERSION         # for Kubernetes Agent (Phase 2)
       value: "1.4.0"
   ```
   And/or point delivery at your private mirror:
   ```yaml
     - name: SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL
       value: "https://mirror.acme.internal/squid"  # mirror copies the GitHub release tree
   ```
3. **Test against one machine first** — pick a non-critical agent, click Upgrade, verify it comes back healthy. Check Seq for `[Upgrade]` log lines.

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
2. **Tentacle version is the source of truth** — `ITentacleVersionRegistry` queries Docker Hub for the actual published Tentacle release, NOT the server's own assembly version (Server and Tentacle release on independent cadences; coupling them is an Octopus hangover we deliberately reject).
3. **Bash script over Halibut RPC** reuses every dollar of investment in the existing deployment pipeline (resilience, log streaming, isolation, mute).
4. **Defense in depth**: distributed lock + cache invalidation + Halibut FullIsolation + 8 documented script exit codes + atomic swap with verified rollback + post-restart version sanity check.
5. **Octopus-aligned where Octopus is right; Octopus-improved where Octopus is documented broken** ([#8842](https://github.com/OctopusDeploy/Issues/issues/8842) Linux fallback dies; no rollback) — none of those failure modes survive into Squid.
