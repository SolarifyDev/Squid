# Frontend Integration Guide — Real-Time Upgrade Progress Streaming (B3)

> **Audience**: frontend engineers wiring the Deployment Target UI to display
> live upgrade progress in the task activity log.
> **Pair this with**: `tentacle-self-upgrade-frontend.md` (the Phase 1 dispatch
> integration).
> **Server status**: B1 (agent emits events) shipped in 1.5.0, B2 (server
> exposes timeline endpoint) shipped in 1.5.x. The FE work in this doc is the
> last piece needed for end-to-end real-time progress visibility.
> **Why this exists**: today's UX after clicking Upgrade is "live Phase A
> output for ~30s, then 20s of dead silence while service restarts, then a
> single line of post-mortem detail". B3 closes the silent gap.

---

## 1 — The endpoint

```
GET /api/machine/{machineId}/upgrade-events
```

**Response**:
```jsonc
{
  "data": {
    "machineId": 21,
    "events": [
      { "timestamp": "2026-04-22T02:57:03Z", "phase": "A", "kind": "start",            "message": "Upgrade to 1.5.0 starting" },
      { "timestamp": "2026-04-22T02:57:04Z", "phase": "A", "kind": "disk-precheck-pass", "message": "Disk space sufficient (>=500MB on /tmp and install dir)" },
      { "timestamp": "2026-04-22T02:57:04Z", "phase": "A", "kind": "method-selected",  "message": "Method: apt" },
      { "timestamp": "2026-04-22T02:57:44Z", "phase": "A", "kind": "scope-exec",       "message": "Detaching to systemd scope for service restart" },
      { "timestamp": "2026-04-22T02:57:47Z", "phase": "B", "kind": "swapped",          "message": "Binary in place via apt; restarting service" },
      { "timestamp": "2026-04-22T02:57:47Z", "phase": "B", "kind": "restart-start",    "message": "Running systemctl restart squid-tentacle" },
      { "timestamp": "2026-04-22T02:58:00Z", "phase": "B", "kind": "healthz-pass",     "message": "Service active and healthz responded OK after 13 second(s)" },
      { "timestamp": "2026-04-22T02:58:02Z", "phase": "B", "kind": "success",          "message": "Upgrade to 1.5.0 successful via apt" }
    ]
  }
}
```

**Empty list** (`events: []`) — semantically: "no upgrade has been observed
on this machine, OR agent is on 1.4.x (pre-events-file), OR server pod
restarted recently (cache cold; refills on next health check). FE should
hide the live-progress UI in this case."

**No 404** — always returns 200 with possibly empty events list. FE doesn't
need to handle "machine not found" vs "no events yet" separately.

---

## 2 — Polling cadence

| Phase | Cadence | Rationale |
|---|---|---|
| Before user clicks Upgrade | Don't poll | No active upgrade, no events to show |
| Right after click | Poll every 2s | Capture the first event (`start`) within 2s of dispatch |
| Active upgrade (events arriving) | Poll every 2s | Sub-2s per-step UI feedback feels real-time |
| Terminal event seen (`success`/`failed`/etc.) | Poll once more, then stop | Confirm no late-arriving events, then disengage |
| 60s without new events | Stop polling | Backstop — if something hung agent-side, polling forever wastes cycles |

The endpoint is idempotent and cheap (in-memory dictionary lookup, no DB,
no RPC). Polling at 2s during an active upgrade is ~30 calls per upgrade —
negligible server load.

---

## 3 — Event kinds reference

Each event has a `kind` field that drives UI rendering. **All known kinds
as of 1.5.0**:

| `kind`                   | Phase | Semantics                                              | Suggested icon |
|--------------------------|-------|--------------------------------------------------------|----------------|
| `start`                  | A     | Upgrade dispatch received; about to choose method      | 🚀 / spinner   |
| `disk-precheck-pass`     | A     | Disk space verified                                    | 💾 / checkmark |
| `method-selected`        | A     | apt / yum / tarball chosen                             | 📦             |
| `method-exhausted`       | A     | (failure) all install methods skipped or failed        | ❌             |
| `scope-exec`             | A     | Detaching to systemd scope — Halibut goes silent next  | 🔌             |
| `swapped`                | B     | New binary in place; restart imminent                  | 🔄             |
| `restart-start`          | B     | `systemctl restart` running                            | ⚡             |
| `restart-fail`           | B     | (failure) restart timed out or returned non-zero       | ❌             |
| `healthz-pass`           | B     | New binary alive + healthz OK                          | ✅             |
| `healthz-fail`           | B     | (failure) restart succeeded but healthz never OK       | ⚠️             |
| `success`                | B     | Terminal happy path — version verified                 | 🎉             |

(Future kinds may be added in Phase 3 — `dpkg-lock-waiting`, `rollback-start`,
`rollback-ok`, etc. FE should treat unknown kinds as generic info events
and render the `message` field directly without an icon.)

---

## 4 — Suggested UX

### Active upgrade panel

```
┌─ Upgrading machine "docker-systemd-test-vm-v2" to 1.5.0 ───────────┐
│                                                                     │
│  ✓ 02:57:03  [A] Upgrade to 1.5.0 starting                         │
│  ✓ 02:57:04  [A] Disk space sufficient                             │
│  ✓ 02:57:04  [A] Method selected: apt                              │
│  ✓ 02:57:44  [A] Detaching to systemd scope                        │
│  ⏳           ↳ Service restarting (typically 5-15s)…                │
│  ✓ 02:57:47  [B] Binary in place via apt; restarting service       │
│  ✓ 02:57:47  [B] Running systemctl restart squid-tentacle          │
│  ✓ 02:58:00  [B] Service active and healthz OK after 13 second(s)  │
│  🎉 02:58:02 [B] Upgrade to 1.5.0 successful via apt                │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Phase A → Phase B transition (the silent gap)

The `scope-exec` event marks the end of Halibut-streamed visibility.
Between `scope-exec` and the next event (`swapped` from Phase B), there's
a deterministic gap (typically 3-5s, sometimes up to 15s on slow restarts).

**Render this gap as a spinner**, NOT a frozen UI. Suggested copy:

> ⏳ Service restarting (Halibut connection re-establishes after restart)…

The next poll that returns a Phase-B event removes the spinner and resumes
the timeline.

### Color coding

- Default events: neutral (gray text + checkmark icon)
- `*-fail` kinds: red text + warning icon
- `success`: green + party emoji
- Last-event-not-success-and-no-new-events-for-30s: amber "stalled" badge

---

## 5 — Sample TypeScript polling implementation

```typescript
type UpgradeEvent = {
  timestamp: string;        // ISO8601 UTC
  phase: 'A' | 'B';
  kind: string;             // see §3 reference
  message: string;
};

async function streamUpgradeEvents(
  machineId: number,
  onEvent: (events: UpgradeEvent[]) => void,
  signal: AbortSignal,
): Promise<void> {
  const TERMINAL_KINDS = new Set(['success', 'method-exhausted', 'restart-fail', 'healthz-fail', 'rollback-ok', 'rollback-fail']);
  const STALL_TIMEOUT_MS = 60_000;
  const POLL_INTERVAL_MS = 2_000;

  let lastChangeAt = Date.now();
  let lastEventCount = 0;

  while (!signal.aborted) {
    const res = await fetch(`/api/machine/${machineId}/upgrade-events`, { signal });
    const { data } = await res.json();
    const events: UpgradeEvent[] = data.events ?? [];

    if (events.length !== lastEventCount) {
      lastChangeAt = Date.now();
      lastEventCount = events.length;
      onEvent(events);
    }

    const lastKind = events[events.length - 1]?.kind;
    if (lastKind && TERMINAL_KINDS.has(lastKind)) {
      // One more poll to catch any late-arriving event, then stop.
      await sleep(POLL_INTERVAL_MS);
      const finalRes = await fetch(`/api/machine/${machineId}/upgrade-events`, { signal });
      onEvent((await finalRes.json()).data.events ?? events);
      return;
    }

    if (Date.now() - lastChangeAt > STALL_TIMEOUT_MS) {
      // Backstop: 60s without any new event = something is wrong server- or
      // agent-side. UI should surface a "stalled" state to the operator.
      throw new Error('Upgrade event stream stalled — check server logs');
    }

    await sleep(POLL_INTERVAL_MS);
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}
```

---

## 6 — Backward compatibility

Pre-1.5.0 agents (1.4.x) do NOT emit events. The `events` list will be
empty. **Detection**: empty events list immediately after a successful
dispatch → assume legacy agent → fall back to:

1. Existing post-mortem polling of `/api/machine/{id}/upgrade-info`
   (returns `currentVersion`).
2. Wait for the agent to reconnect via Halibut after the restart.
3. Show "Upgrade dispatched — checking outcome…" indefinitely until
   `currentVersion` matches the target or a timeout (suggest 5 min).

This degrades gracefully — no UI breakage, just less granular feedback
until the agent fleet upgrades to 1.5.0+.

---

## 7 — Edge cases

| Scenario | What you'll observe | UI handling |
|---|---|---|
| Operator reloads the page mid-upgrade | First poll returns the FULL accumulated event list (not a delta) | Render all events at once, then poll for new ones — same code path |
| Server pod restart during upgrade | `events` may temporarily empty, then refill from next health check | Show a "reconnecting" badge for ~30s before assuming failure |
| Two operators click Upgrade simultaneously | Server-side Redis lock prevents the duplicate; one operator gets a 409, the other proceeds | Show normal progress for the winner; show "another operator just triggered an upgrade — refreshing" for the loser |
| Agent goes offline mid-upgrade | Phase B events stop arriving but `last-upgrade.json` still says IN_PROGRESS for up to 10 min | Show "stalled" badge after 60s; A2 server-side reconciliation auto-clears the lock at the 10-min mark |
| Upgrade rolls back (Phase 3+) | New `rollback-*` kinds arrive | Render in red; final state will be `rollback-ok` or `rollback-fail` |

---

## 8 — Testing checklist for the FE

- [ ] Smoke test: trigger an upgrade in a test environment and watch the
      poll log — events should arrive within 2s of each agent-side
      transition.
- [ ] Halibut-gap UX: verify the spinner appears between `scope-exec` and
      the next Phase B event (typically 3-15s).
- [ ] Stall detection: pause the agent's tentacle process during Phase A
      (`docker exec test-vm pkill -STOP -f Squid.Tentacle`); after 60s the
      UI should show "stalled". Resume (`pkill -CONT`) and the stream
      should auto-resume.
- [ ] 1.4.x compat: target a machine still running 1.4.x and confirm the
      empty-events fallback path renders cleanly.
- [ ] Browser refresh mid-upgrade: events should re-render from cache,
      not require a fresh dispatch.

---

## 9 — Future enhancements (Phase 3 / 1.6.0+)

- Per-event detail expansion (click a `restart-start` event to see the
  full systemctl output) — would require server-side log streaming (B4
  in the upgrade roadmap).
- Push instead of poll — current polling at 2s is fine, but a SignalR
  push would reduce per-upgrade chatter from ~30 GETs to a single
  WebSocket subscription. Worth doing if dashboard-style "watch all
  upgrades" view is built.
- Cancel button — clicking would call a future
  `POST /api/machine/{id}/upgrade-cancel` endpoint that DEL's the Redis
  lock and signals the agent to abort. Not in 1.5.x scope.
