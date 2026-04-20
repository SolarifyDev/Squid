# Frontend Integration Guide — Tentacle Self-Upgrade

> **Audience**: frontend engineers wiring the Deployment Target UI to the
> upgrade feature.
> **Pair this with**: `tentacle-self-upgrade-design.md` (backend architecture).
> **Status**: server-side Phase 1 shipped on branch `tentacle-self-upgrade`.
> **Frontend status**: server has the dispatch endpoint; the "discover which
> machines can be upgraded" side still has gaps — called out in §9 below with
> proposed endpoint additions.

---

## 1 — Quick reference

| What you want to do | Endpoint | Method |
|---|---|---|
| List machines (shows per-machine health) | `/api/machines/list` | GET |
| Trigger upgrade on one machine | `/api/machines/{id}/upgrade` | POST |
| Run a health check (refresh version, etc.) | `/api/machines/{id}/health-check` | POST |
| Get agent connection status | `/api/machines/connection-status?subscriptionId=…` | GET |
| Get latest K8s agent version (K8s only) | `/api/machines/latest-agent-version` | GET |

> **Known gaps** (Phase 2): the machine list does NOT currently include the
> agent's running version, and there's no generic "latest upgradeable
> version for this machine's style" endpoint. The FE has to work around
> this until §9's proposed additions land. See "Discovery flow" below.

---

## 2 — The upgrade endpoint

### Request

```http
POST /api/machines/{machineId}/upgrade
Content-Type: application/json
X-Space-Id: {yourSpaceId}
X-API-KEY: {apiKey}

{
  "targetVersion": "1.4.0",          // optional — server auto-resolves latest if omitted
  "allowDowngrade": false             // optional — default false
}
```

**Body fields**:

| Field | Type | Required | Description |
|---|---|---|---|
| `targetVersion` | `string\|null` | ❌ | Specific semver (`MAJOR.MINOR.PATCH[-pre][+build]`). If omitted or null, server queries Docker Hub for the latest published Tentacle for this machine's communication style. |
| `allowDowngrade` | `boolean` | ❌ | Default `false`. Set to `true` for emergency revert scenarios ("1.4.2 has a bug, go back to 1.4.0"). Same-version requests are still no-ops regardless of this flag. |

**Headers**:

| Header | Required | Notes |
|---|---|---|
| `X-Space-Id` | ✅ | Must match the space the machine belongs to (permission check uses it). The controller deliberately ignores any `spaceId` you put in the body to prevent cross-space privilege tricks. |
| `X-API-KEY` | ✅ | Standard Squid API key. User must have `MachineEdit` permission in the target space. |

### Response

All responses are HTTP `200 OK` with a body wrapper:

```typescript
interface UpgradeMachineResponse {
  data: UpgradeMachineResponseData;
  code: number;          // 200 on success (transport-level); business outcome is inside data.status
  msg: string;
}

interface UpgradeMachineResponseData {
  machineId: number;
  machineName: string;
  currentVersion: string;    // empty string if agent has never health-checked yet
  targetVersion: string;     // resolved target (after auto-resolution)
  status: MachineUpgradeStatus;
  detail: string;            // human-readable, ALWAYS present, safe to render directly in a toast
}

enum MachineUpgradeStatus {
  Upgraded = 0,          // agent is now running targetVersion
  AlreadyUpToDate = 1,   // no dispatch happened (same version OR downgrade without flag)
  NotSupported = 2,      // no strategy for this communication style (e.g. Ssh)
  Failed = 3,            // attempted but failed — see detail
  Initiated = 4,         // dispatched; agent disconnected mid-script (expected during restart)
}
```

### HTTP-level errors (NOT `200 OK`)

| Status | Meaning | FE action |
|---|---|---|
| `404 Not Found` | machineId doesn't exist | Remove stale list row; toast "Machine no longer exists" |
| `401 Unauthorized` | API key / session expired | Redirect to login |
| `403 Forbidden` | User lacks `MachineEdit` in that space | Toast "Insufficient permissions" |
| `409 Conflict` | (rare) name/state conflict from validation | Show server's error detail |
| `500 Internal Server Error` | Unexpected server failure | Toast + retry with backoff |

> Business outcomes (`Failed`, `NotSupported`, etc.) come back as `200 OK`
> with a non-success `status`. Don't treat `200` as "upgrade worked" — read
> `data.status`.

---

## 3 — Discovery flow: "Which machines can be upgraded?"

### 3.1 — Today (Phase 1 workaround)

Current `GET /api/machines/list` returns `MachineDto[]` without an agent
version field. So the FE cannot compute "upgrade available" purely from
the list. Two workarounds:

**Option A (recommended for Phase 1) — "upgrade always available" button**

Show an "Upgrade" button on every machine row whose health status suggests
the agent is reachable (`Healthy` / `HasWarnings`). The server's
`AlreadyUpToDate` response is the authoritative signal — if the user
clicks Upgrade and the agent is already on the latest, they get a clear
toast saying so.

Pros: no extra API call per row.
Cons: operator sees a button that might no-op.

**Option B — client-side version query per row (expensive)**

Call `GET /api/machines/latest-agent-version` once (it's K8s-only today
but returns a single string) AND somehow fetch each machine's current
version. Today there's no server endpoint for "current agent version by
machine id" — you'd have to trigger health checks or wait for Phase 2.

**We recommend Option A for now.**

### 3.2 — Phase 2 (proposed server additions)

The server team should add these to close the discovery gap:

```typescript
// GET /api/machines/list  — ADD `agentVersion` to MachineDto
interface MachineDto {
  id: number;
  name: string;
  // ... existing fields
  agentVersion: string;    // NEW: empty if agent hasn't health-checked yet
}

// GET /api/machines/{id}/upgrade-info  — NEW endpoint
interface UpgradeInfoResponseData {
  machineId: number;
  currentVersion: string;       // from runtime capabilities cache
  latestAvailableVersion: string; // from ITentacleVersionRegistry
  canUpgrade: boolean;          // true if latest > current
  reason: string;               // why canUpgrade is what it is (for tooltips)
}
```

With these, FE can show a badge:

```tsx
{machine.agentVersion && upgradeInfo.canUpgrade && (
  <Badge color="warning">New version {upgradeInfo.latestAvailableVersion} available</Badge>
)}
```

File a ticket referencing this doc §3.2 when the FE needs the badge.

---

## 4 — Trigger interaction (what to render while the call is in flight)

The upgrade call is **synchronous from the HTTP perspective** — it blocks
until the agent either confirms success, rolls back, or disconnects
mid-script. Typical durations:

| Scenario | Typical duration |
|---|---|
| `AlreadyUpToDate` (no dispatch) | ~50ms |
| `NotSupported` | ~50ms |
| `Failed` (validation — e.g. malformed version) | ~50ms |
| Happy path (`Upgraded` / `Initiated`) | 30s – 3 min |
| `Failed` (network / download / agent) | up to 5 min (Halibut script timeout) |

### Recommended UX

```
┌─────────────────────────────┐
│  [Confirm dialog]           │
│  "Upgrade machine-foo?      │
│   Current: 1.3.9            │
│   Target: 1.4.0 (auto)      │
│   [ ] Advanced ▾            │
│       [input] Target version│
│       [ ] Allow downgrade   │
│                             │
│   [Cancel]  [Upgrade]       │
└─────────────────────────────┘
         │ click
         ▼
┌─────────────────────────────┐
│  [Spinner on the row]       │
│  "Upgrading machine-foo…"   │
│  (hide Upgrade button,      │
│   disable row actions)      │
│   [disabled link] Cancel    │
└─────────────────────────────┘
         │ response arrives
         ▼
┌─────────────────────────────┐
│  [Status-colored toast]     │
│  green: "✅ Upgraded to 1.4.0"│
│  blue:  "ℹ️ Already on 1.4.0"│
│  amber: "⏳ Upgrade initiated; refreshing in 30s…"  │
│  red:   "❌ Upgrade failed: <detail>"               │
└─────────────────────────────┘
```

### Timeout handling

Set the HTTP request timeout to **5 minutes** for the upgrade call.
If the request actually hits that timeout, the server's Halibut dispatch
has already hit its own timeout, and the result is unknown — show "Upgrade
timed out; refresh to see agent state" and trigger a health check.

```typescript
const response = await fetch(`/api/machines/${id}/upgrade`, {
  method: "POST",
  body: JSON.stringify(body),
  signal: AbortSignal.timeout(5 * 60 * 1000),    // 5 min
});
```

### Cancellation

User clicks "Cancel" on the spinner: abort the fetch with `AbortController`.
The server's strategy will observe the cancellation and propagate
`OperationCanceledException`. The agent's script might STILL finish (we
can't kill remote bash mid-execution), so follow up with a health check
to see the actual post-cancel state.

```typescript
const controller = new AbortController();
const response = fetch(url, { signal: controller.signal });
// on cancel click:
controller.abort();
// after abort resolves:
await fetch(`/api/machines/${id}/health-check`, { method: "POST" });
```

---

## 5 — Response handling matrix

```typescript
async function handleUpgradeResponse(resp: UpgradeMachineResponseData) {
  switch (resp.status) {
    case MachineUpgradeStatus.Upgraded:
      toast.success(`Upgraded ${resp.machineName} to ${resp.targetVersion}`);
      // Server has invalidated its cache. The next scheduled health check
      // will refresh UI. Optionally force a health check NOW:
      await fetch(`/api/machines/${resp.machineId}/health-check`, { method: "POST" });
      refreshMachineList();
      break;

    case MachineUpgradeStatus.AlreadyUpToDate:
      // Two sub-reasons in the detail:
      //  (a) "already on version X" — genuinely up-to-date
      //  (b) "higher than requested ... AllowDowngrade" — blocked downgrade
      if (resp.detail.includes("AllowDowngrade")) {
        // Offer the operator the escape hatch
        const confirm = await prompt(
          `${resp.detail}\n\nForce downgrade anyway?`,
          { cancelLabel: "Cancel", confirmLabel: "Force downgrade" }
        );
        if (confirm) {
          return triggerUpgrade(resp.machineId, { ...originalBody, allowDowngrade: true });
        }
      } else {
        toast.info(resp.detail);
      }
      break;

    case MachineUpgradeStatus.Initiated:
      // Agent disconnected mid-script — EXPECTED on service restart. The
      // actual success/fail is unknowable until the next health check.
      toast.info(`${resp.machineName} is restarting; verifying in 30s…`, { duration: 30_000 });
      setTimeout(async () => {
        await fetch(`/api/machines/${resp.machineId}/health-check`, { method: "POST" });
        refreshMachineList();
      }, 30_000);
      break;

    case MachineUpgradeStatus.NotSupported:
      // E.g. SSH target, or K8s agent (Phase 1 stub). The detail includes
      // remediation (usually "run helm upgrade directly" for K8s).
      toast.warning(resp.detail, { duration: 10_000 });
      break;

    case MachineUpgradeStatus.Failed:
      // The detail is already operator-actionable (see backend runbook).
      // We recommend logging the machineId + targetVersion on your side
      // for support.
      toast.error(resp.detail, { duration: 15_000 });
      console.error("[Upgrade Failed]", {
        machineId: resp.machineId,
        currentVersion: resp.currentVersion,
        targetVersion: resp.targetVersion,
        detail: resp.detail
      });
      break;
  }
}
```

### Detail strings — stable contract

The backend guarantees `detail` is human-readable and safe to surface
directly. Key fragments the FE might branch on (ordered by frequency):

| Fragment | What it signals | Recommended FE response |
|---|---|---|
| `"already on version"` | Up-to-date, genuine | Info toast |
| `"higher than requested"` + `"AllowDowngrade"` | Downgrade blocked | Prompt for force-downgrade |
| `"agent disconnected mid-script"` | Initiated path | Schedule health check, refresh |
| `"currently being upgraded by another request"` | Redis lock held | Retry button, ~5min |
| `"not valid semver"` | Bad TargetVersion input | Keep dialog open, highlight input |
| `"No CommunicationStyle field"` | Machine misconfigured | Direct to machine edit page |
| `"dispatch failed before the agent acknowledged"` | Agent unreachable | Direct to agent diagnostics |
| `"Upgrade script failed (exit N)"` | Script-level failure | See exit-code table in design doc §7.1 |

---

## 6 — Progress tracking (Initiated flow)

The `Initiated` status is inherently asynchronous — server can't tell
whether the agent finished the upgrade successfully because the polling
connection died mid-script. Three approaches for FE, in order of
increasing fidelity:

### 6.1 — Passive wait for next health check

Simplest. The server invalidates the runtime capabilities cache on
`Initiated`; the next scheduled health check (typically 5 min) picks up
the new version. UI will eventually reflect it.

### 6.2 — Active poll via on-demand health check (recommended)

On receipt of `Initiated`, schedule a health check 30 seconds out:

```typescript
async function waitForUpgradeToSettle(machineId: number): Promise<string> {
  const delays = [30_000, 30_000, 60_000, 60_000];    // ~3 min total
  for (const delay of delays) {
    await sleep(delay);
    await fetch(`/api/machines/${machineId}/health-check`, { method: "POST" });
    // Refetch the list to get updated agent version (once Phase 2 exposes it)
    const latest = await refreshMachineRow(machineId);
    if (latest.health === "Healthy") return latest.agentVersion;
  }
  throw new Error("Upgrade did not settle within 3 min");
}
```

### 6.3 — Server-Sent Events / WebSocket (Phase 2)

Not implemented today. Phase 2 could push `UpgradeProgress` events. For
now, §6.2's poll pattern is the contract.

---

## 7 — Complete TypeScript types

Drop this into your frontend types folder — matches the backend DTOs
verbatim.

```typescript
// Request
export interface UpgradeMachineCommand {
  /** Omit or pass null → server auto-resolves latest from Docker Hub */
  targetVersion?: string | null;
  /** Default false. Set true to force emergency downgrades. */
  allowDowngrade?: boolean;
}

// Response
export enum MachineUpgradeStatus {
  Upgraded = 0,
  AlreadyUpToDate = 1,
  NotSupported = 2,
  Failed = 3,
  Initiated = 4,
}

export interface UpgradeMachineResponseData {
  machineId: number;
  machineName: string;
  currentVersion: string;   // may be empty if machine hasn't been health-checked
  targetVersion: string;
  status: MachineUpgradeStatus;
  detail: string;
}

export interface UpgradeMachineResponse {
  data: UpgradeMachineResponseData;
  code: number;
  msg: string;
}
```

---

## 8 — Example React hook

```typescript
import { useCallback, useState } from "react";

interface UseMachineUpgradeReturn {
  isUpgrading: boolean;
  lastResult: UpgradeMachineResponseData | null;
  upgrade: (machineId: number, opts?: UpgradeMachineCommand) => Promise<UpgradeMachineResponseData>;
  forceDowngrade: (machineId: number, targetVersion: string) => Promise<UpgradeMachineResponseData>;
}

export function useMachineUpgrade(): UseMachineUpgradeReturn {
  const [isUpgrading, setIsUpgrading] = useState(false);
  const [lastResult, setLastResult] = useState<UpgradeMachineResponseData | null>(null);

  const upgrade = useCallback(
    async (machineId: number, opts: UpgradeMachineCommand = {}) => {
      setIsUpgrading(true);
      try {
        const res = await fetch(`/api/machines/${machineId}/upgrade`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "X-Space-Id": String(currentSpaceId()),
          },
          body: JSON.stringify(opts),
          signal: AbortSignal.timeout(5 * 60 * 1000),
        });

        if (res.status === 404) throw new Error("Machine not found");
        if (res.status === 403) throw new Error("Insufficient permissions");
        if (!res.ok) throw new Error(`Upgrade request failed: ${res.status}`);

        const body: UpgradeMachineResponse = await res.json();
        setLastResult(body.data);
        return body.data;
      } finally {
        setIsUpgrading(false);
      }
    },
    []
  );

  const forceDowngrade = useCallback(
    (machineId: number, targetVersion: string) =>
      upgrade(machineId, { targetVersion, allowDowngrade: true }),
    [upgrade]
  );

  return { isUpgrading, lastResult, upgrade, forceDowngrade };
}
```

Usage in a machine row component:

```tsx
function MachineRowUpgradeButton({ machine }: { machine: MachineDto }) {
  const { isUpgrading, upgrade } = useMachineUpgrade();

  const handleClick = async () => {
    const confirmed = await showConfirmDialog({
      title: `Upgrade ${machine.name}?`,
      body: `This will update the agent to the latest Tentacle version.`,
    });
    if (!confirmed) return;

    try {
      const result = await upgrade(machine.id);
      handleUpgradeResponse(result);
    } catch (err) {
      toast.error(`Upgrade request failed: ${err.message}`);
    }
  };

  return (
    <Button onClick={handleClick} disabled={isUpgrading || machine.healthStatus === "Unavailable"}>
      {isUpgrading ? <Spinner /> : "Upgrade"}
    </Button>
  );
}
```

---

## 9 — Gaps to close (Phase 2 server-side asks)

The FE can't ship the "upgrade available" badge without the following
additions. File these as server-side Phase 2 tickets:

### 9.1 — `agentVersion` on `MachineDto`

Add a string field to `MachineDto` populated from the runtime capabilities
cache. Empty when the agent has never health-checked.

```csharp
// src/Squid.Message/Models/Deployments/Machine/MachineDto.cs
public class MachineDto {
    // ... existing fields
    public string AgentVersion { get; set; } = string.Empty;
}
```

Wire up in `GetMachinesRequestHandler` (likely 1 line — read from
`IMachineRuntimeCapabilitiesCache.TryGet(machineId).AgentVersion`).

### 9.2 — `GET /api/machines/{id}/upgrade-info` endpoint

A focused read endpoint for the upgrade button:

```csharp
// Proposed request/response
public class GetUpgradeInfoRequest : IRequest, ISpaceScoped {
    public int? SpaceId { get; set; }
    public int MachineId { get; set; }
}

public class GetUpgradeInfoResponseData {
    public int MachineId { get; set; }
    public string CurrentVersion { get; set; }         // runtime cache
    public string LatestAvailableVersion { get; set; } // ITentacleVersionRegistry
    public bool CanUpgrade { get; set; }               // Compare via SemVer
    public string Reason { get; set; }                 // e.g. "Latest (1.4.0) is newer than current (1.3.9)"
}

// GET /api/machines/{id}/upgrade-info  →  GetUpgradeInfoResponse
```

Handler: inject `ITentacleVersionRegistry` + `IMachineRuntimeCapabilitiesCache`,
compare via `SemVer.TryParse` + `CompareTo`, return the tuple.

Performance: once per row-render is OK (cached 10min on the registry
side via fan-out dedupe — 50 machines on page load collapses to 1
Docker Hub round-trip).

### 9.3 — (Optional) `POST /api/machines/upgrade-batch`

Bulk upgrade N machines in one call. Phase 2 feature. Tickets linked
from design doc §5 Phase 2 roadmap.

### 9.4 — (Optional) WebSocket / SSE progress stream

Push `UpgradeProgress` events (started / script-line-N / completed /
rolled-back). Phase 2. Until available, §6.2's polling pattern is the
canonical contract.

---

## 10 — Glossary

* **CommunicationStyle**: the machine's transport type (TentaclePolling,
  TentacleListening, KubernetesAgent, KubernetesApi, Ssh). Server looks
  at this to pick the right upgrade strategy.
* **Tentacle**: the Squid agent binary installed on target machines.
  Different packages per OS (Linux x64, Linux arm64, Windows, K8s pod).
* **AlreadyUpToDate**: no-op — either genuinely up-to-date OR a blocked
  downgrade (check detail).
* **Initiated**: upgrade DISPATCHED, outcome unknown at the moment the
  HTTP response was built. Agent's polling connection dropped when the
  service restarted mid-script — expected behaviour.
* **Health check**: out-of-band probe that the agent is alive and reports
  current version. Triggered automatically on schedule OR manually via
  `POST /api/machines/{id}/health-check`.
* **Runtime capabilities cache**: server-local (per-replica) in-memory
  cache of per-machine metadata including `AgentVersion`. Invalidated
  automatically post-upgrade so the next health check refreshes it.

---

## 11 — Checklist for FE PR

Before merging the machine-upgrade UI:

- [ ] `UpgradeMachineStatus` enum values match backend (0–4)
- [ ] 5-minute timeout on the fetch call
- [ ] Abort controller wired to a cancel affordance
- [ ] Status-differentiated toast styling (green / blue / amber / red)
- [ ] `Initiated` path triggers a 30s delayed health check
- [ ] `AlreadyUpToDate` + `AllowDowngrade` detail triggers a confirm dialog
- [ ] HTTP 404 / 403 / 500 each have distinct UI treatment
- [ ] Row-level spinner hides the Upgrade button until response returns
- [ ] `X-Space-Id` header set correctly on every call
- [ ] `detail` string is surfaced verbatim (not summarised/reformatted)
  — backend promises it's operator-ready
- [ ] No assumption that `currentVersion` is non-empty (can be blank on
  cold cache)
- [ ] Unit test for the response handler that covers all 5 status values

---

## 12 — Questions / escalations

* Server behaviour questions → `docs/tentacle-self-upgrade-design.md` (§4
  failure modes, §7 operator API, §7.1 operator runbook)
* Test the contract against a dev server before merging UI PRs
* File tickets referencing this doc's section number for any API addition
  requested
