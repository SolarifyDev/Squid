# Tentacle E2E Scenario Matrix — Source of Truth

This is the authoritative ledger for **every** Tentacle E2E scenario across Windows + Linux. The goal: green test suite alone gives confidence to ship — no manual UI smoke testing needed.

## Status legend

| Status | Meaning |
|---|---|
| ⚪ Planned | Identified, not yet implemented |
| 🟡 WIP | Under active development |
| 🟢 Covered | Implemented, passing on macOS skip-guard, awaiting Windows verification |
| ✅ Verified | Implemented + green on the target OS runner |

## Fidelity legend (per Rule 12)

| Tier | What it means |
|---|---|
| 🟢 H | High-fidelity: real prod class + real OS resource |
| 🟡 M | Medium: inline mirror with drift detector OR real prod + mocked external dep |
| 🔵 F | Fixture-only: tests test infra, not production |

---

## Section A — Installation Scripts

`install-tentacle.sh` (Linux) / `install-tentacle.ps1` (Windows). One-liner installer that downloads zip/tarball, extracts to install dir, registers as Windows service / systemd unit.

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| A1.h | Default `latest` install completes; binary at install dir; service registered + RUNNING | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A1.u1 | Network blackhole during download → script exits non-zero with "Could not download" | ✓ | ✓ | 12.K | ⚪ | 🟢 | use local HTTP fixture serving 503 |
| A1.u2 | Mirror returns 404 for the requested version → fallback URL tried; both fail → exits with all-URLs-tried message | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A2.h | `--version 1.6.0` → installs that exact version (verified via `--probe-version`) | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A2.u1 | `--version <bogus>` → exits non-zero, no service registered | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A2.u2 | `--version 1.6.0` (un-prefixed tag 404) → falls back to `v1.6.0` tag → succeeds | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A3.h | `--install-dir <user-path>` extracts to user-owned path; no admin needed | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A3.u1 | `--install-dir <read-only>` → clear permission error, no partial install | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A4.h | `DOWNLOAD_BASE` env points at private mirror → uses it instead of github.com | ✓ | ✓ | 12.K | ⚪ | 🟢 | use local HTTP fixture as mirror |
| A5.h | Linux musl detection on Alpine → picks `linux-musl-x64` | — | ✓ | 12.K | ⚪ | 🟢 | docker fixture; alpine image |
| A5.u1 | musl detection misses, defaults to glibc → binary fails to start with "symbol not found" → diagnostic logged | — | ✓ | 12.K | ⚪ | 🟢 | |
| A6.h | Windows ARM64 → `win-arm64` RID picked | ✓ | — | 12.K | ⚪ | 🟢 | conditional skip on x64-only runner |
| A6.u1 | 32-bit Windows env detection → friendly "not supported" exit | ✓ | — | 12.K | ⚪ | 🟢 | |
| A7.h | `--no-service-install` → extracts binary, prints next-step hint, does NOT register service | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A8.h | Re-run installer over existing install → succeeds (idempotent) | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A8.u1 | Re-run while service is RUNNING → script must stop service first (or fail with clear message) | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| A9.h | Windows firewall rule `Squid Tentacle (Listening)` added on TCP 10933 | ✓ | — | 12.K | ⚪ | 🟢 | verify via `Get-NetFirewallRule` |
| A9.u1 | Firewall rule already exists → no error, "skipping" message logged | ✓ | — | 12.K | ⚪ | 🟢 | |
| A10.h | apt repo configured: `/etc/apt/sources.list.d/squid.list` + key file | — | ✓ | 12.K | ⚪ | 🟢 | |
| A10.u1 | apt repo unreachable → fallback to direct tarball, install still succeeds | — | ✓ | 12.K | ⚪ | 🟢 | |
| A11.h | sudoers rule installed: `/etc/sudoers.d/squid-tentacle-upgrade` passes `visudo -c` | — | ✓ | 12.K | 🟢 | 🟢 | already covered by `InstallTentacleSudoersTests` (unit) — promote to E2E |
| A11.u1 | Generated sudoers rule fails `visudo -c` → file NOT installed, warning logged | — | ✓ | 12.K | ⚪ | 🟢 | inject bad SERVICE_USER name |
| A12.h | Service user `squid-tentacle` created via `useradd -r` | — | ✓ | 12.K | ⚪ | 🟢 | |
| A12.u1 | Service user already exists → skip creation (idempotent) | — | ✓ | 12.K | ⚪ | 🟢 | |

**Section A total: 24 scenarios**

---

## Section B — Service Lifecycle

`squid-tentacle service install/uninstall/start/stop/status` — the SCM/systemd CLI surface.

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| B1.h | `service install` → SCM/systemd entry created | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.2 covered Windows; need Linux |
| B1.u1 | binary path missing → install fails, no SCM/systemd entry registered | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.2 covered Windows |
| B2.u1 | Re-install on existing service → 1073 (Windows) / "unit already exists" (systemd) | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.2 covered Windows |
| B3.h | `service start` → state becomes RUNNING (Windows) / active (systemd) | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | |
| B3.u1 | Service binary crashes on OnStart → SCM 1053 surfaced | ✓ | ✓ | 12.G/12.K | ⚪ | 🟢 | use bogus binary |
| B4.h | `service stop` → state becomes STOPPED (Windows) / inactive (systemd) | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | |
| B4.u1 | Service ignores Stop signal → SCM/systemd timeout → SIGKILL fallback | ✓ | ✓ | 12.G/12.K | ⚪ | 🟢 | |
| B5.h | `service status` (registered + running) → exit 0 | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.2 |
| B5.u1 | `service status` (not registered) → exit non-zero | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.2 |
| B6.h | `service uninstall` (no --purge) → SCM entry gone, config files preserved | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.5 covered Windows |
| B6.u1 | `service uninstall` on absent service → 1060 (Windows) / "no such unit" (systemd) → mapped to 0 | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.2 covered Windows |
| B7.h | `service uninstall --purge` → SCM gone + config gone + registry entry gone | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.5 covered Windows |
| B7.u1 | `--purge` on absent service still cleans config files | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.5 covered Windows |
| B7.u2 | `--purge` with locked config file → graceful warning, SCM still uninstalled | ✓ | ✓ | 12.G/12.K | ⚪ | 🟢 | |
| B8.h | Auto-restart policy applied (sc qfailure shows RESTART / systemd Restart=on-failure) | ✓ | ✓ | 12.G/12.K | ✅ Win | 🟢 | G.2 covered Windows |
| B8.u1 | Service crashes 3x within window → SCM/systemd stops retrying | ✓ | ✓ | 12.G/12.K | ⚪ | 🟢 | uses crashing test binary |
| B9.h | Multi-instance: `--instance Foo` and `--instance Bar` co-exist | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| B9.u1 | `--instance Foo` after Foo already exists → 1073 / "already exists" | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| B10.h | Custom `--service-name` overrides default | ✓ | ✓ | 12.K | ⚪ | 🟢 | |

**Section B total: 19 scenarios** (10 ✅ on Windows; ~9 still planned)

---

## Section C — Registration

`squid-tentacle register --server X --api-key Y --role R --environment E [--comms-url Z] [--thumbprint T]`. Establishes identity with the Squid server and persists config to disk.

**Requires Phase 12.H StubSquidServer.**

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| C1.h | Listening register against stub server → exit 0; config file persisted with thumbprint + server URL | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `Listening_HappyPath_PersistsConfigAndCallsServer` |
| C1.u1 | Server responds 401 → exit non-zero with "API key rejected" | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `Listening_ServerReturns401_ExitsNonZero`; surfaces as HttpRequestException |
| C1.u2 | Server unreachable → exit non-zero with "could not connect" | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `ServerUnreachable_ExitsNonZero` |
| C2.h | Polling register with `--comms-url` → config file persisted; subscription ID created; cert thumbprint registered | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `Polling_HappyPath_PersistsConfigAndCallsServer` |
| C2.u1 | `--comms-url` unreachable → exit non-zero | ✓ | ✓ | 12.I | ⚪ | 🟢 | shares unreachable-server failure mode with C1.u2 |
| C3.u1 | Missing `--server` → CLI usage error exit 1 | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `NoServerUrl_ExitsWithUsageError` |
| C4.h | Self-signed server cert + `--thumbprint <fingerprint>` pin → handshake succeeds | ✓ | ✓ | 12.I.2 | ⚪ | 🟢 | requires HTTPS stub; deferred to follow-up |
| C4.u1 | Wrong `--thumbprint` → handshake rejects with "thumbprint mismatch" | ✓ | ✓ | 12.I.2 | ⚪ | 🟢 | requires HTTPS stub |
| C4.u2 | No `--thumbprint`, server cert untrusted → handshake fails with "untrusted issuer" | ✓ | ✓ | 12.I.2 | ⚪ | 🟢 | requires HTTPS stub |
| C5.h | Config file persists at `PlatformPaths.GetInstanceConfigPath` for Default instance | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered alongside C1.h (same code path) |
| C5.h2 | Config file persists at per-instance path for `--instance Foo` | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `NamedInstance_PersistsConfigAtInstancePath` |
| C5.u1 | Config dir read-only → exit non-zero with permission error | ✓ | ✓ | 12.I.2 | ⚪ | 🟢 | needs OS-specific read-only dir setup; deferred |
| C6.h | Re-register over existing config → updates fields, preserves cert/subscription | ✓ | ✓ | 12.I.2 | ⚪ | 🟢 | needs cert reload edge-case wiring; deferred |
| C7.h | `--role A,B,C` (comma-separated) accumulates; multiple `--environment` accumulates | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `CommaSeparatedRoles_AllPersistedInConfig` + regression-pin `RepeatedRoleFlags_OnlyLastValueWins_KnownBug` |
| C7.u1 | Empty role list → CLI rejects | ✓ | ✓ | 12.I.2 | ⚪ | 🟢 | currently allowed by impl; verify desired contract first |
| C8.h | `register` adds machine to InstanceRegistry | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `Register_AddsInstanceToRegistry` |
| C-bonus | `--bearer-token` sets Authorization header (mutually exclusive with --api-key) | ✓ | ✓ | 12.I | 🟢 | 🟢 | covered as `BearerToken_AttachesAuthorizationHeader` |
| C9.h | Linux: sudo register → ownership handover to `squid-tentacle` user | — | ✓ | 12.I.2 | ⚪ | 🟢 | runs as root, asserts uid:gid post-register; deferred to Linux phase |
| C9.u1 | Linux: register without sudo, default config dir → permission error | — | ✓ | 12.I.2 | ⚪ | 🟢 | deferred to Linux phase |

**Section C total: 18 scenarios**

---

## Section D — Deployment Execution

The core operator value: server dispatches a script → tentacle runs it → results return. Tests both communication styles (Listening, Polling) on both OSes.

**Requires Phase 12.H StubSquidServer + production `Squid.Tentacle.exe` binary running as a service.**

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| D1.h | Listening: server dispatches script (PowerShell on Win / Bash on macOS+Linux) → output captured + exit 0 | ✓ | ✓ | 12.J.D.1 | 🟢 | 🟢 | `Listening_EchoScript_OutputCapturedAndExitZero` (cross-OS via `OsScript.Echo`) |
| D1.u1 | Listening: script `exit 42` → exit code propagated EXACTLY (not normalised to 1) | ✓ | ✓ | 12.J.D.1 | 🟢 | 🟢 | `Listening_NonZeroExit_PropagatesExactExitCode` |
| D2.h | Polling: server queues script for polling agent → agent picks up + executes | ✓ | ✓ | 12.J.D.1 | 🟢 | 🟢 | `Polling_EchoScript_OutputCapturedAndExitZero` |
| D2.u1 | Polling: agent disconnects mid-script → server treats as Initiated, polls status | ✓ | ✓ | 12.J.E.2 | ⚪ | 🟢 | needs HalibutScriptObserver disconnect handling |
| D3.h | Multi-line stdout fully captured + order preserved | ✓ | ✓ | 12.J.D.1 | 🟢 | 🟢 | `Listening_MultiLineOutput_AllLinesCaptured` (with order pin) |
| D4.h | Stderr captured separately and tagged as ProcessOutputSource.StdErr | ✓ | ✓ | 12.J.D.1 | 🟢 | 🟢 | `Listening_StderrOutput_CapturedAndTaggedAsStdErr` |
| D5.h | Calamari packaged execution: `DeployByCalamari.ps1` template runs end-to-end | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | needs Calamari binary or stub |
| D5.u1 | Calamari package SHA mismatch → reject with clear error | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | |
| D6.h | Single file transferred via `ScriptFile[]` and accessible to script | ✓ | ✓ | 12.J.D.4 | 🟢 | 🟢 | `Listening_SingleFileTransfer_AgentWritesAndScriptReads` |
| D6.h2 | Multiple files in single dispatch all transferred | ✓ | ✓ | 12.J.D.4 | 🟢 | 🟢 | `Listening_MultipleFileTransfer_AllFilesAvailableToScript` (round-6 fix: `Write-Output (Get-Content -Raw)` for PS) |
| D6.u1 | File transfer interrupted → task fails with transfer error | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | needs Halibut DataStream interruption injection |
| D7.h | Output variable parsed: `##squid[setVariable name='X' value='Y']` → ServiceMessageParser extracts | ✓ | ✓ | 12.J.D.3 | 🟢 | 🟢 | `Listening_PlainOutputVariable_RoundTripsToProductionParser` |
| D7.h2 | Sensitive output variable: `sensitive='True'` → IsSensitive flag set | ✓ | ✓ | 12.J.D.3 | 🟢 | 🟢 | `Listening_SensitiveOutputVariable_FlaggedByProductionParser` |
| D7.u1 | Output variable with special characters via base64 encoding | ✓ | ✓ | 12.J.D.3 | 🟢 | 🟢 | `Listening_OutputVariableWithBase64Encoding_RoundTripsCorrectly` |
| D8.h | Variable substitution: server-side `#{Foo}` expansion before dispatch | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | server-side concern; covered by unit tests |
| D8.u1 | Variable not defined → empty substitution + warning | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | server-side concern |
| D9.h | Long-running script (3s sleep) → late output still captured | ✓ | ✓ | 12.J.D.2 | 🟢 | 🟢 | `Listening_LongRunningScript_CompletesAndCapturesAllOutput` |
| D9.u1 | Script exceeds timeout → server cancels via Halibut → tentacle terminates process | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | needs CancelScript RPC |
| D10.h | Concurrent dispatches isolated by ScriptTicket | ✓ | ✓ | 12.J.D.2 | 🟢 | 🟢 | `Listening_ConcurrentDispatches_OutputsIsolatedByTicket` (round-5 fix: 50ms stagger for pwsh spawn) |
| D11.h | Network blip mid-script (Listening) → server retry succeeds | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | needs Halibut runtime restart |
| D11.u1 | Network blip + max retries exhausted → task fails with network error | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | |
| D12.h | Exit code 42 propagated exactly to server (not normalised to 1) | ✓ | ✓ | 12.J.D.1 | 🟢 | 🟢 | covered by D1.u1 |
| D13.h | Unicode (CJK + em-dash + emoji) round-trips through Halibut + shell | ✓ | ✓ | 12.J.D.2 | 🟢 | 🟢 | `Listening_UnicodeOutput_PreservedThroughHalibutAndShell` |
| D14.h | Working directory of script execution = isolated per-task temp dir | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | |
| D14.u1 | Temp dir not writable → task fails with clear error | ✓ | ✓ | 12.J.D.5 | ⚪ | 🟢 | |

**Section D total: 26 scenarios × 2 OS-specific variants where applicable ≈ 52 tests**

---

## Section E — Upgrade Flow

Server → tentacle wrapper → Phase A (download) → Phase B (binary swap + restart) → status report via `last-upgrade.json`. The most complex pipeline; round 1-3 of Phase 12.G fixed real production bugs in this area.

**Requires Phase 12.H StubSquidServer + local release-mirror HTTP fixture + apt/yum stub for Linux.**

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| E1.h-dispatch | Win: `WindowsTentacleUpgradeStrategy.UpgradeAsync` happy path → returns Initiated; wrapper dispatched + observed | ✓ | — | 12.J.E.1 | 🟢 | 🟢 | `Listening_UpgradeAsync_HappyPath_ReturnsInitiatedAndDispatchesWrapper` |
| E1.h-unreachable | UpgradeAsync against unreachable agent → returns Failed (NOT Initiated) | ✓ | — | 12.J.E.1 | 🟢 | 🟢 | `UpgradeAsync_AgentUnreachable_ReturnsFailed` |
| E1.h-noversion | UpgradeAsync with empty target version → ValidateRequest rejects pre-dispatch | ✓ | — | 12.J.E.1 | 🟢 | 🟢 | `UpgradeAsync_EmptyTargetVersion_ReturnsFailedWithoutDispatch` |
| E1.h | Win zip method: server dispatches upgrade → Phase A downloads → Phase B swaps + restarts → new version reported | ✓ | — | 12.J.E.2 | ⚪ | 🟢 | needs release-mirror HTTP fixture |
| E1.h2 | Linux tarball method: same flow with .tar.gz | — | ✓ | 12.J | ⚪ | 🟢 | |
| E1.u1 | Download URL 404 → status reports Failed with download-error detail | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E2.h | Linux apt method: package installed via `apt-get install -y squid-tentacle=1.6.0` | — | ✓ | 12.J | ⚪ | 🟢 | docker fixture with stub apt repo |
| E2.u1 | apt lock contention → wait + retry; eventually succeeds | — | ✓ | 12.J | ⚪ | 🟢 | |
| E2.u2 | apt repo missing → fallback to tarball method | — | ✓ | 12.J | ⚪ | 🟢 | |
| E3.h | Linux dnf method: `dnf install -y squid-tentacle-1.6.0-1.x86_64` | — | ✓ | 12.J | ⚪ | 🟢 | docker fixture with stub yum repo |
| E3.u1 | dnf repo unreachable → fallback to tarball | — | ✓ | 12.J | ⚪ | 🟢 | |
| E4.h | Already at target version → wrapper short-circuits, no Phase B | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E5.u1 | Target version not in release index → wrapper fails with "version not found" | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E6.h | Phase B Stop-Service → Move-Item swap → Start-Service → marker reports new version | ✓ | — | 12.G | ✅ | 🟡 | inline mirror; drift detector exists; promote to high-fidelity by running real .ps1 |
| E6.h2 | Linux Phase B: stop systemd → swap binary → start systemd → reports new version | — | ✓ | 12.J | ⚪ | 🟢 | |
| E6.u1 | Phase B mid-flight crash → .bak rollback restores old version → status reports Failed-with-rollback | ✓ | ✓ | 12.J | ⚪ | 🟢 | inject failure between Move-Item swap and Start-Service |
| E7.h | After successful upgrade, service auto-restart picks up new binary on next reboot too | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E7.u1 | New binary's OnStart crashes → SCM 1053 → status reports Failed → rollback restores old binary | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E8.h | `last-upgrade.json` written with success outcome → server reads on next capabilities probe | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E8.h2 | `last-upgrade.json` written with failure outcome → server reads → operator sees in UI | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E8.u1 | `last-upgrade.json` corrupt → server treats as "no recent upgrade", logs warning | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E9.h | Capabilities probe after upgrade reports new version → server cache refreshes | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E9.u1 | Capabilities probe times out → server retries; eventually marks Unreachable | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E10.u1 | Concurrent server-side upgrade dispatches → Redis lock prevents dual; second returns "already in progress" | ✓ | ✓ | 12.J | ⚪ | 🟡 | unit-tested; promote to E2E |
| E11.u1 | Concurrent agent-side dispatches (rare — operator + scheduled together) → tentacle lock file prevents dual; second is no-op | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E11.u2 | Stale tentacle lock file (from crashed process) → next dispatch detects + breaks the lock | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E12.h | SHA companion file fetch + hash verification → matching SHA accepts | ✓ | — | 12.G | ✅ | 🟡 | covered by `WindowsUpgradeShaVerifyE2ETests` |
| E12.u1 | SHA mismatch → reject + log + status Failed with "checksum failed" | ✓ | ✓ | 12.J | ⚪ | 🟢 | inject corrupt zip |
| E12.u2 | SHA companion 404 → opportunistic fetch falls through, install proceeds (current behaviour) | ✓ | — | 12.G | ✅ | 🟢 | |
| E13.h | Custom `SQUID_TARGET_*_DOWNLOAD_BASE_URL` env → uses mirror; HTTPS warning absent for HTTPS URL | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E13.u1 | Custom URL non-HTTPS → warning logged, install still proceeds | ✓ | ✓ | 12.J | ⚪ | 🟢 | |
| E14.u1 | Wrapper mid-script Halibut disconnect → server treats as Initiated; outcome via last-upgrade.json next probe | ✓ | ✓ | 12.J | ⚪ | 🟡 | unit-tested; promote |
| E15.h | Upgrade preserves `instances/<name>.config.json` (cert + subscription unchanged) | ✓ | ✓ | 12.J | ⚪ | 🟢 | critical regression target |
| E16.h | Linux apt rollback: snapshot `.deb` saved before upgrade; `dpkg -i --force-downgrade` restores | — | ✓ | 12.J | ⚪ | 🟢 | |
| E17.h | Linux dnf rollback: `dnf downgrade -y squid-tentacle` restores prior version | — | ✓ | 12.J | ⚪ | 🟢 | |

**Section E total: 32 scenarios × 2 OS where applicable ≈ 52 tests**

---

## Section F — Health & Capabilities

Server-side capabilities probe + tentacle's `/healthz` endpoint.

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| F1.h | Listening probe returns agent version + supported services | ✓ | ✓ | 12.J.E.2 | 🟢 | 🟢 | `Listening_CapabilitiesProbe_ReturnsAgentReportedVersion` (PR #195) |
| F1.h2 | Polling probe returns agent version | ✓ | ✓ | 12.J.E.2 | 🟢 | 🟢 | `Polling_CapabilitiesProbe_ReturnsAgentReportedVersion` |
| F1.metadata | Probe response carries Metadata dictionary (os/flavor) | ✓ | ✓ | 12.J.E.2 | 🟢 | 🟢 | `Listening_CapabilitiesResponse_CarriesMetadataDictionary` |
| F1.u1 | Tentacle process down → probe returns "agent unreachable" | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| F2.u1 | Probe times out → mapped to "agent unresponsive" | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| F3.cache | Repeated probes within 60s TTL return CACHED response (`[CacheResponse(60)]` honored) | ✓ | ✓ | 12.J.E.2 | 🟢 | 🟢 | `Listening_RepeatedProbes_WithinCacheTtl_ReturnCachedResponse` |
| F3.h | Cache invalidates after 60s TTL → fresh probe shows new version | ✓ | ✓ | 12.L | ⚪ | 🟢 | needs >60s test (slow) |
| F4.h | `/healthz` 200 OK after service start | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| F4.u1 | `/healthz` returns 503 during startup → server retries, eventually green | ✓ | ✓ | 12.L | ⚪ | 🟢 | |

**Section F total: 6 scenarios × 2 OS ≈ 12 tests**

---

## Section G — Multi-Instance

Two or more instances on the same host without collision.

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| G1.h | Install instance Foo + Bar on same host → both get unique service names + config dirs | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| G1.h2 | Foo + Bar registered against different servers → independent identities | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| G2.h | Uninstall Foo → Bar still works | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| G3.u1 | Install Foo when Foo already exists → 1073 / "already exists" with clear error | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| G4.u1 | Corrupt `instances.json` → graceful read, "Default" instance falls back | ✓ | ✓ | 12.L | ⚪ | 🟢 | |
| G4.u2 | Missing `instances.json` → first register creates it | ✓ | ✓ | 12.L | ⚪ | 🟢 | |

**Section G total: 6 scenarios × 2 OS ≈ 8 tests** (some shared)

---

## Section H — Boundary / Failure Injection

Edge cases that bit operators in production.

| ID | Scenario | Win | Lin | Phase | Status | Tier | Notes |
|---|---|---|---|---|---|---|---|
| H1.u1 | Disk full during install → clear error, no partial state, install dir cleaned up | ✓ | ✓ | 12.K | ⚪ | 🟢 | use small loopback fs |
| H2.u1 | Antivirus quarantines exe mid-extract → install errors with "binary missing post-extract" | ✓ | — | 12.K | ⚪ | 🟢 | |
| H3.u1 | Non-admin / non-root user runs install with default install dir → friendly permission error with elevation hint | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| H4.u1 | Clock skew between server and tentacle (5 min) → cert validation still works (within tolerance) | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| H4.u2 | Clock skew >24h → cert validation fails with clear error | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| H5.u1 | DNS resolution failure for server URL → "could not resolve hostname" | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| H6.u1 | Transparent proxy in front of github.com → install succeeds via proxy | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| H6.u2 | Linux apt repo behind transparent proxy → `99-squid-direct.conf` bypass works | — | ✓ | 12.K | ⚪ | 🟢 | |
| H7.u1 | Listening tentacle behind firewall blocking inbound 10933 → register fails with "could not connect" | ✓ | ✓ | 12.K | ⚪ | 🟢 | |
| H8.u1 | Server cert expired → handshake fails with "cert expired" message | ✓ | ✓ | 12.K | ⚪ | 🟢 | |

**Section H total: 10 scenarios × ~1.5 OS ≈ 16 tests**

---

## Grand Total

| Section | Scenarios | Estimated tests | Currently 🟢 Covered |
|---|---|---|---|
| A — Install scripts | 24 | 24 | 0 |
| B — Service lifecycle | 19 | 19 | 12 (G.2 + G.5) |
| C — Registration | 18 | 18 | 9 (Phase 12.I) |
| D — Deployment execution | 26 | 52 | 13 (Phase 12.J.D.1-4) |
| E — Upgrade flow | 32 | 52 | 6 (3 wrapper E2E from Phase 12.G + 3 J.E.1 dispatch tests) |
| F — Health & capabilities | 6 | 12 | 4 (J.E.2 capabilities probe via PR #195, P0-unblocked) |
| G — Multi-instance | 6 | 8 | 0 |
| H — Boundary cases | 10 | 16 | 0 |
| **Total** | **141 unique scenarios** | **≈201 tests** | **75 (37% covered)** |

---

## Phase rollout map

| Phase | Sections | Status | Tests added | Cumulative |
|---|---|---|---|---|
| 12.G rounds 1-6 | Round-trip stability fixes (B + E partial) | ✅ Verified | 17 | 17 |
| 12.G.2 | B — `WindowsServiceHost` SCM lifecycle | ✅ Verified | +12 | 29 |
| 12.G.5 | B — `ServiceCommand uninstall --purge` | ✅ Verified | +3 | 32 |
| 12.H | StubSquidServer + smoke + REST | ✅ Verified | +8 | 40 |
| 12.I | C — register CLI | ✅ Verified | +10 | 50 |
| 12.J.D.1 | D — deploy core | ✅ Verified | +5 | 55 |
| 12.J.D.2 | D — long-running, concurrent, unicode | ✅ Verified | +3 | 58 |
| 12.J.D.3 | D — output variables | ✅ Verified | +3 | 61 |
| 12.J.D.4 | D — file transfer | ✅ Verified | +2 | 63 |
| 12.J.E.1 | E — `UpgradeAsync` dispatch round-trip | ✅ Verified | +3 | 66 |
| 12.K.1 | A — install-tentacle.ps1 E2E + production em-dash fix | ✅ Verified (PR #192) | +3 | 69 |
| 12.L.1 | G — multi-instance Windows | ✅ Verified (PR #193) | +2 | 71 |
| **P0 fix** | **🐛 Halibut cache-key bug** — every health check + liveness probe was silently broken | ✅ Verified (PR #194) | **+0 production tests, but 3 fix-pin** | 71 (+3 fix-pin) |
| 12.J.E.2 | F — capabilities probe (UNBLOCKED by P0) | ✅ Verified (PR #195) | +4 | **75** |
| 12.J.E.3+ | E — full upgrade lifecycle (download + Phase B + last-upgrade.json) | ⚪ Planned | ~10-15 | ~85-90 |
| 12.J.E.3+ | E — upgrade methods (zip/apt/dnf) + rollback + lock | ⚪ Planned | ~30 | ~108 |
| 12.J.D.5 | D — Calamari + variable substitution + cancellation | ⚪ Planned | ~10 | ~118 |
| 12.K | A (install scripts) + H (boundary) | ⚪ Planned | ~40 | ~158 |
| 12.L | B (lifecycle remainder) + F (health) + G (multi-instance) | ⚪ Planned | ~28 | ~186 |

**Currently shipped on `phase12.G-real-e2e-hardening` branch: 66/66 verified on Windows + macOS.** This represents the highest-frequency operator workflows (install, register, deploy, upgrade dispatch) at high-fidelity tier 🟢 — every test drives real production code against real OS resources.

---

## Update protocol

- When a scenario moves status (Planned → WIP → Covered → Verified), update the row.
- When a new scenario is identified, add a row with `Planned` status.
- When a phase ships, update the rollout map's "Cumulative" column.
- PRs that touch Tentacle code SHOULD reference the matrix IDs they affect (e.g. "Implements C1.h, C1.u1, C1.u2").
