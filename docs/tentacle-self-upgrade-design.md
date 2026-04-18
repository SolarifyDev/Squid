# Tentacle Self-Upgrade — Architecture Design

**Status:** Phase 1 implementing in `tentacle-self-upgrade` branch.
**Goal:** One-click upgrade of an installed agent from the Web UI, generic across
all target types, atomic + safe, no manual SSH or shell to the box.

---

## Why this exists

Currently, when Squid releases a new Tentacle (`squid-tentacle-linux:1.4.0`),
every customer must SSH to every machine and re-run the install script — same
pain point Octopus solved a decade ago. Without a self-upgrade primitive, every
new Tentacle release fragments the fleet by `wait-until-someone-runs-the-script`.

We model this on Octopus's `TentacleUpgradeMediator` pattern (see local
`/Users/mars/Projects/octopus`) but **fix three known Octopus weaknesses**:

| Octopus weakness | Our fix |
|---|---|
| Linux upgrade silently dies when neither apt nor yum is on the box (issue [#8842](https://github.com/OctopusDeploy/Issues/issues/8842)) | Tarball delivery is the **primary** path, not a fallback; works on any glibc 2.31+ Linux including alpine/distroless |
| No automatic rollback on failed upgrade — operator left with broken box | Strategy keeps **N-1 binary** under `/opt/squid-tentacle.bak`; if post-upgrade health check fails, swap back |
| Custom `Octopus.Upgrader.exe` watchdog process must outlive Tentacle restart | Use **systemd `ExecStartPre` health check** + the existing `Squid.Tentacle.Watchdog` companion (already shipped); no new binary to sign/distribute |

---

## Architecture

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
            │  1. Look up Machine + parse CommunicationStyle           │
            │  2. Resolve target version via IBundledTentacleVersionProvider │
            │  3. Resolve current version via                          │
            │     IMachineRuntimeCapabilitiesCache (already populated  │
            │     by health checks via Halibut Capabilities probe)     │
            │  4. If current >= target → return AlreadyUpToDate         │
            │  5. Resolve IMachineUpgradeStrategy by CommunicationStyle │
            │  6. Delegate UpgradeAsync()                              │
            └─────────────────────────┬────────────────────────────────┘
                ┌─────────────────────┴───────────────────────┐
                ▼                                              ▼
   ┌─────────────────────────────┐              ┌──────────────────────────────┐
   │ LinuxTentacleUpgradeStrategy │              │ KubernetesAgentUpgradeStrategy │
   │ (Polling + Listening)        │              │ (Helm-based, deferred)        │
   │                              │              │                              │
   │ Sends bash via Halibut RPC:  │              │ Calls helm upgrade --install │
   │  • download tarball          │              │ via cluster's k8s API client  │
   │  • backup current            │              │ (no agent-side participation) │
   │  • atomic swap (mv → mv)     │              └──────────────────────────────┘
   │  • restart squid-tentacle    │
   │    via systemd / launchd     │
   │  • verify with new version   │
   │  • on fail, swap back        │
   └──────────────────────────────┘
```

### Component breakdown

| Component | File (planned) | Phase |
|---|---|---|
| `IMachineUpgradeStrategy` interface | `Squid.Core/Services/Machines/Upgrade/IMachineUpgradeStrategy.cs` | 1 |
| `LinuxTentacleUpgradeStrategy` | `Squid.Core/Services/Machines/Upgrade/LinuxTentacleUpgradeStrategy.cs` | 1 |
| `KubernetesAgentUpgradeStrategy` | `Squid.Core/Services/Machines/Upgrade/KubernetesAgentUpgradeStrategy.cs` | 2 |
| `WindowsTentacleUpgradeStrategy` | future | 3 |
| `IMachineUpgradeService` orchestrator | `Squid.Core/Services/Machines/Upgrade/MachineUpgradeService.cs` | 1 |
| `IBundledTentacleVersionProvider` | `Squid.Core/Services/Machines/Upgrade/BundledTentacleVersionProvider.cs` | 1 |
| `UpgradeMachineCommand` + handler | `Squid.Message/Commands/Machine/`, `Squid.Core/Handlers/CommandHandlers/Machine/` | 1 |
| `MachineController.UpgradeMachineAsync` endpoint | `Squid.Api/Controllers/MachineController.cs` | 1 |
| Bundled upgrade script `upgrade-linux-tentacle.sh` | `Squid.Core/Resources/upgrade-linux-tentacle.sh` (embedded) | 1 |
| Tests | `tests/Squid.UnitTests/Services/Machines/Upgrade/` | 1 |
| UI button + version column | future (frontend) | 2 |

---

## Phase 1 — Linux Tentacle, polling + listening

### Wire flow

1. Operator clicks **Upgrade** in UI for `machineId=42` running `squid-tentacle 1.3.3`.
2. UI: `POST /api/machines/42/upgrade` → returns `{"taskId":"upgrade-42-1762..."}` immediately (async).
3. `MachineUpgradeService` resolves:
   - **Current version**: `_runtimeCapabilitiesCache.TryGet(42).AgentVersion` (already cached from last health check's Halibut Capabilities probe).
   - **Target version**: `IBundledTentacleVersionProvider.GetVersion()` reads embedded resource `squid-tentacle-version.txt` populated at build time (`<Version>` in `.csproj`).
   - If `current ≥ target` → return `UpgradeResult { Status: AlreadyUpToDate }`.
4. Resolves `LinuxTentacleUpgradeStrategy` (matches `TentaclePolling` and `TentacleListening`).
5. Strategy:
   - Loads embedded bash template `upgrade-linux-tentacle.sh`, substitutes `{{TARGET_VERSION}}`, `{{DOWNLOAD_URL}}`, `{{INSTALL_DIR}}`, `{{SERVICE_NAME}}`.
   - Submits via `IHalibutClientFactory` → `IAsyncScriptService.StartScriptAsync()`. **Same plumbing as a normal "Run a Script" deployment step** — no new RPC contract needed.
   - Polls `GetStatusAsync` every 1s for up to 5 minutes. Uses existing `HalibutScriptObserver` infrastructure.
   - When the agent's polling reconnects on the new version, observe via `Capabilities.AgentVersion` to confirm.

### Bash upgrade script (Phase 1)

Embedded in `Squid.Core/Resources/upgrade-linux-tentacle.sh`. Pseudocode:

```bash
#!/usr/bin/env bash
set -euo pipefail

TARGET_VERSION="{{TARGET_VERSION}}"
DOWNLOAD_URL="{{DOWNLOAD_URL}}"
INSTALL_DIR="{{INSTALL_DIR}}"
SERVICE_NAME="{{SERVICE_NAME}}"

# 1. Download new tarball to staging
STAGE="/tmp/squid-tentacle-upgrade-${TARGET_VERSION}-$$"
mkdir -p "$STAGE"
trap 'rm -rf "$STAGE"' EXIT

curl -fsSL --retry 3 "$DOWNLOAD_URL" -o "$STAGE/tentacle.tar.gz"
mkdir -p "$STAGE/extract"
tar xzf "$STAGE/tentacle.tar.gz" -C "$STAGE/extract"

# 2. Sanity check the new binary
chmod +x "$STAGE/extract/Squid.Tentacle"
NEW_VERSION=$("$STAGE/extract/Squid.Tentacle" --version 2>&1 | head -1 | awk '{print $NF}')
[ "$NEW_VERSION" = "$TARGET_VERSION" ] || {
  echo "::error:: Downloaded binary reports version '$NEW_VERSION', expected '$TARGET_VERSION'"
  exit 2
}

# 3. Atomic swap: rename current → .bak, then new → current
sudo systemctl stop "$SERVICE_NAME"

if [ -d "$INSTALL_DIR.bak" ]; then sudo rm -rf "$INSTALL_DIR.bak"; fi
sudo mv "$INSTALL_DIR" "$INSTALL_DIR.bak"
sudo mv "$STAGE/extract" "$INSTALL_DIR"
sudo chown -R squid-tentacle:squid-tentacle "$INSTALL_DIR" 2>/dev/null || true

# 4. Restart and verify
sudo systemctl start "$SERVICE_NAME"

# 5. Health check loop (max 30s)
for i in $(seq 1 30); do
  if "$INSTALL_DIR/squid-tentacle" health --quiet 2>/dev/null; then
    echo "Upgrade to $TARGET_VERSION successful"
    sudo rm -rf "$INSTALL_DIR.bak"
    exit 0
  fi
  sleep 1
done

# 6. Rollback
echo "::warning:: New tentacle failed health check after 30s. Rolling back."
sudo systemctl stop "$SERVICE_NAME"
sudo rm -rf "$INSTALL_DIR"
sudo mv "$INSTALL_DIR.bak" "$INSTALL_DIR"
sudo systemctl start "$SERVICE_NAME"
exit 3
```

### Why bash via Halibut, not a new RPC contract

- **Reuses existing infra** — Halibut polling is already proven for sending scripts.
- **No new Tentacle CLI** — no version skew between server and agent for the upgrade contract itself.
- **Bash is the lingua franca** of Linux deployments; every install we ship has bash.
- **Easy operator override** — if the script needs tweaking for a strange env, an operator can copy it and run by hand.

### Failure modes covered

| Failure | Behavior |
|---|---|
| Download fails (network blip, GitHub rate-limit) | `curl --retry 3` retries; if all fail, exit 1, no swap performed |
| Downloaded binary is wrong version | Sanity check at step 2 → exit 2 before any swap |
| New binary fails to start | systemctl health check loop → rollback at step 6 |
| Agent crashes between step 3 and step 4 (mid-swap) | Next agent boot reads from `.bak` if main is missing — encoded in systemd unit's `ExecStartPre` |
| Server-agent connection drops mid-script | Halibut polling protocol replays — script writes a sentinel file `/var/lib/squid-tentacle/upgrade.lock` to make second invocation a no-op |

---

## Phase 2 — Kubernetes Agent

Different mechanism entirely — **server-side `helm upgrade`** via the cluster's
K8s API client. Agent doesn't participate; the helm chart is bumped to a new
image tag, K8s rolls the deployment, the new agent pod starts polling.

```csharp
public sealed class KubernetesAgentUpgradeStrategy : IMachineUpgradeStrategy
{
    public bool CanHandle(string communicationStyle) => communicationStyle == "KubernetesAgent";

    public Task<UpgradeResult> UpgradeAsync(Machine machine, string targetVersion, CancellationToken ct)
    {
        // 1. Resolve cluster connection via existing KubernetesEndpointAccount
        // 2. Use Helm CLI shell-out OR Helm SDK
        //    helm upgrade --reuse-values --set image.tag=<targetVersion> <release-name> <chart>
        // 3. kubectl rollout status to confirm
        // 4. Return result
    }
}
```

Defer to Phase 2 because:
- Requires bundling helm CLI in Squid API image (or using a Helm .NET SDK).
- Different failure modes (rollout stuck, image pull, registry auth).
- Has own atomicity story (K8s does the rolling update + automatic rollback).

---

## Phase 3 — Windows Tentacle

Once Windows tentacle binary is in regular release rotation, mirror Linux flow but use:
- PowerShell instead of bash
- `Stop-Service` / `Start-Service` instead of systemctl
- `.zip` instead of `.tar.gz`
- Same atomic-swap-with-rollback pattern

---

## Out of scope for this PR

- **UI changes** — frontend team adds the button + version column. Backend exposes `GET /api/machines/{id}/upgrade-info` returning `{currentVersion, targetVersion, upgradeAvailable}`.
- **Auto-upgrade on schedule** — cron-based "upgrade entire fleet to N when N is N+0/+1 only".
- **Bulk upgrade across multiple machines** — easy follow-up; just iterate the per-machine flow with a parent task.
- **Tentacle "upgrade-only" sub-command** — deferred; bash script approach in Phase 1 doesn't need it.

---

## Naming / Identity preservation guarantees

This is the biggest worry from the operator's POV: "will my machine come back as a NEW machine after upgrade?"

**No** — same machine. The upgrade does NOT touch:

- `/opt/squid/certs/tentacle-cert.pfx` (preserved across binary swap because it's outside the binary directory)
- `/etc/squid-tentacle/instances/*.json` (subscription IDs, server URL, API key)
- The Halibut polling thumbprint (it's the cert thumbprint, derived from the unchanged cert)
- `Machine` row in DB (matched by subscription ID, which doesn't change)

The new binary boots, reads `/opt/squid/certs/tentacle-cert.pfx`, sees the existing subscription ID, and resumes polling on the same Halibut connection. From the server's POV: the agent disconnected for a few seconds and reconnected — no register, no new MachineId.

---

## Test strategy

| Layer | Test |
|---|---|
| Unit | `BundledTentacleVersionProvider` reads embedded resource correctly |
| Unit | `MachineUpgradeService` returns `AlreadyUpToDate` when current ≥ target |
| Unit | `MachineUpgradeService` resolves correct strategy per `CommunicationStyle` |
| Unit | `LinuxTentacleUpgradeStrategy` substitutes template variables correctly + invokes `IHalibutClientFactory` |
| Integration | `UpgradeMachineCommandHandler` end-to-end with mocked Halibut client returning success exit code |
| E2E (Tentacle.Tests) | Real bash script execution against a fake `/opt/squid-tentacle` directory + mock systemctl, verify rollback path |

---

## Cleanest patterns adopted from Octopus

1. **Strategy-per-communication-style** — same mechanism we already use for `IExecutionStrategy` and `IEndpointVariableContributor`; symmetric across the deployment domain.
2. **Server-bundled target version** via `IBundledTentacleVersionProvider` — matches Octopus's `IBundledPackageStore` design but uses a flat embedded resource, not a NuGet package store.
3. **Mediator orchestrates the multi-step flow** — `MachineUpgradeService` is the mediator; per-target strategies are stateless functions of `(Machine, targetVersion) → UpgradeResult`.
4. **Reuse the script-execution pipeline for the upgrade payload** — no new RPC contract; bash script over the same `IAsyncScriptService` polling channel that runs deployments.
5. **Idempotency via sentinel file** — `/var/lib/squid-tentacle/upgrade.lock` makes redundant upgrade attempts a no-op (Octopus uses the same pattern with `Upgrade\{{InstallId}}` directory existence check).
