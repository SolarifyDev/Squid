# ==============================================================================
# Squid Tentacle self-upgrade script (Windows)
# ------------------------------------------------------------------------------
# Sent over a Halibut polling RPC by the server's WindowsTentacleUpgradeStrategy
# at ScriptIsolationLevel.FullIsolation, so the agent serialises
# this behind any in-flight deployment scripts — we never restart a tentacle
# mid-deploy.
#
# Placeholders ({{...}}) are filled by the server before transmission.
#
# Mirrors the Linux upgrade-linux-tentacle.sh structure for operator-readability
# parity: same Phase A → Phase B split, same INSTALL_OK / INSTALL_METHOD
# variables, same status-file shape (WindowsUpgradeStatusStorage
# layout: %ProgramData%\Squid\Tentacle\upgrade\last-upgrade.json).
#
# ARCHITECTURE:
#   Phase A (in tentacle process / Halibut sees logs):
#     • Pre-flight (arch, status file setup, idempotency lock).
#     • INSTALL_METHODS block (server-injected): ordered chocolatey → MSI →
#       zip-marker dispatch (ships chocolatey + MSI;
#       ships zip-marker only). The first method whose detection branch matches
#       sets $INSTALL_OK = $true.
#     • If $INSTALL_METHOD = 'zip', the existing zip download/verify/extract
#       block runs (separate from the marker so its ~80 lines stay in the
#       template, not in C#).
#
#   Phase B (after Phase A — see "Detach mechanism" note below):
#     • Conditional swap: zip method requires Move-Item .\bak / Move-Item
#       .\staging; chocolatey/MSI methods are no-ops here (the package
#       manager already wrote %ProgramFiles%\Squid Tentacle).
#     • Stop-Service squid-tentacle.
#     • Move-Item swap.
#     • Start-Service squid-tentacle.
#     • Health poll + version verify.
#     • Status file at %ProgramData%\Squid\Tentacle\upgrade\last-upgrade.json
#       (Octopus parity: server reads on next health check via
#       Capabilities RPC).
#
# Detach mechanism:
#   When the SCM does Stop-Service squid-tentacle, the service's main process
#   (PID running this script as a child) is terminated. To survive the
#   restart, the WindowsTentacleUpgradeStrategy will wrap the
#   template invocation in a detach mechanism (likely Task Scheduler one-shot
#   task running as SYSTEM, equivalent to Linux's `systemd-run --scope`).
#   This template is written ASSUMING the detach has already happened — it
#   runs end-to-end synchronously without trying to self-detach.
#
# Status progression (matches Linux):
#   IN_PROGRESS → SWAPPED → SUCCESS
#                          → ROLLED_BACK
#                          → ROLLBACK_NEEDED
#                          → ROLLBACK_CRITICAL_FAILED
#
# Exit codes (Halibut-visible from Phase A):
#   0   — dispatched to Phase B OR no-op (already on target version)
#   1   — unsupported architecture
#   2   — download failure (zip method only)
#   3   — missing binary in extracted archive
#   5   — insufficient disk space
#   7   — SHA256 mismatch (only when EXPECTED_SHA256 is non-empty)
#   8   — Start-Service post-swap failed → rollback fired (J.E.6)
#   9   — Healthcheck timeout in FATAL mode → rollback fired (J.E.7)
#   12  — Windows version too old (PowerShell 5.0+ required, Server 2016+)
#   13  — failed to acquire upgrade lock (concurrent upgrade in progress)
#   14  — no install method succeeded (zip + future chocolatey/MSI all skipped or failed)
#   15  — insufficient privileges (must run as Administrator or LocalSystem)
# ==============================================================================

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Identity gate ──────────────────────────────────────────────
# The WindowsTentacleUpgradeStrategy wraps invocation in a
# Task Scheduler one-shot task with `/RU SYSTEM` — equivalent to Linux's
# `systemd-run --scope` running as root. If we see a non-elevated identity
# here, the wrapper failed silently and Phase B's `Stop-Service` /
# `Move-Item` will fail at a confusing layer. Fail fast with a clear message.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isElevated = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
$isSystem = $identity.IsSystem

if (-not $isElevated -and -not $isSystem) {
    Write-Host "::error:: Upgrade script must run as Administrator or LocalSystem (current identity: $($identity.Name), elevated=$isElevated, system=$isSystem). The strategy schedules this script via Task Scheduler with /RU SYSTEM; if you see this error the wrapper did not detach correctly."
    exit 15
}

# ── Arch detection MUST run before DOWNLOAD_URL is consumed ───────────────────
# $env:PROCESSOR_ARCHITECTURE on a 64-bit OS is "AMD64" (x64) or "ARM64".
# 32-bit Windows is NOT supported — docs analysis concluded
# x86 has no real audience in 2026 (Server 2012 R2 was the last 32-bit
# Windows Server, EOL 2023).
$arch = $env:PROCESSOR_ARCHITECTURE

switch -Regex ($arch) {
    '^AMD64$' { $RID = 'win-x64'; break }
    '^ARM64$' { $RID = 'win-arm64'; break }
    default {
        Write-Host "::error:: Unsupported architecture: $arch (Squid Tentacle ships for win-x64 and win-arm64 only)"
        exit 1
    }
}

# ── Placeholders filled by WindowsTentacleUpgradeStrategy before transmission ─
$TARGET_VERSION   = '{{TARGET_VERSION}}'
$DOWNLOAD_URL     = '{{DOWNLOAD_URL}}'
$EXPECTED_SHA256  = '{{EXPECTED_SHA256}}'
$INSTALL_DIR      = '{{INSTALL_DIR}}'
$SERVICE_NAME     = '{{SERVICE_NAME}}'
$HEALTHCHECK_URL  = '{{HEALTHCHECK_URL}}'
# Retry count substituted as a numeric literal (no quotes) — `[int]` cast
# would fail on a quoted-and-cast empty string if the placeholder ever
# defaults to empty.
$HEALTHCHECK_RETRIES = {{HEALTHCHECK_RETRIES}}
# Healthcheck failure mode. Substituted by the strategy as `$true` /
# `$false` (PowerShell boolean literals — no quotes) so this assignment
# is type-safe regardless of operator's env var format. Default `$false`
# = warning + proceed (matches Octopus Tentacle); `$true` = strict =
# rollback on timeout. See WindowsTentacleUpgradeStrategy.HealthcheckFatalEnvVar.
$HEALTHCHECK_FATAL = {{HEALTHCHECK_FATAL}}

#  contract: %ProgramData%\Squid\Tentacle\upgrade\
$STATUS_DIR  = Join-Path $env:ProgramData 'Squid\Tentacle\upgrade'
$STATUS_FILE = Join-Path $STATUS_DIR 'last-upgrade.json'
$LOCK_FILE   = Join-Path $STATUS_DIR 'upgrade.lock'
$LOG_FILE    = Join-Path $STATUS_DIR 'upgrade.log'

if (-not (Test-Path $STATUS_DIR)) {
    New-Item -ItemType Directory -Path $STATUS_DIR -Force | Out-Null
}

# ── Status file helper — atomic write via temp+rename ────────────────────────
function Write-UpgradeStatus {
    param(
        [string] $Status,
        [string] $Detail = '',
        [string] $InstallMethod = '',
        [int]    $ExitCode = 0
    )

    $payload = @{
        schemaVersion = 2
        status        = $Status
        targetVersion = $TARGET_VERSION
        installMethod = $InstallMethod
        detail        = $Detail
        exitCode      = $ExitCode
        startedAt     = (Get-Date).ToUniversalTime().ToString('o')
        scriptPid     = $PID
    } | ConvertTo-Json -Depth 5

    # Temp + rename for atomicity — readers (Squid server's
    # WindowsUpgradeStatusStorage via Capabilities RPC) can never see a
    # half-written JSON.
    $temp = "$STATUS_FILE.tmp"
    Set-Content -Path $temp -Value $payload -Encoding UTF8 -Force
    Move-Item -Path $temp -Destination $STATUS_FILE -Force
}

function Append-UpgradeLog {
    param([string] $Line)

    $stamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    Add-Content -Path $LOG_FILE -Value "[$stamp] $Line"
    Write-Host $Line
}

# ── Rollback helper (J.E.6) ──────────────────────────────────────────────────
# Restores the previous binary from .bak when Phase B can't bring the new
# binary up. Three failure modes drive this:
#   1. Start-Service throws (new binary's OnStart raised → SCM 1067 / "the
#      service did not start in a timely fashion"). E.g., new binary has a
#      broken DI graph that throws at construction time.
#   2. Service start succeeded but new binary's healthcheck never returned
#      200 within $HEALTHCHECK_RETRIES * 2 seconds — currently a warning
#      (matches Octopus Tentacle's behaviour: capabilities probe will detect
#      a missing version on the next health probe). A future env-var-gated
#      "fatal" mode will make this a rollback trigger.
#   3. Move-Item swap failed (rare: file in use, permission). Phase A
#      already validated $extractDir exists; the only Move-Item failure
#      mode here is filesystem-level. Best-effort: try restore.
#
# Rollback assumes: $bakDir, $INSTALL_DIR, $SERVICE_NAME, $INSTALL_METHOD,
# $TARGET_VERSION are in scope (defined earlier in Phase A / Phase B).
#
# Status writes:
#   - ROLLED_BACK              — clean restoration: old service is RUNNING again
#   - ROLLBACK_NEEDED          — no .bak available (somehow), can't auto-restore
#   - ROLLBACK_CRITICAL_FAILED — restored .bak but old service won't start either
function Invoke-Rollback {
    param(
        [Parameter(Mandatory)] [string] $Reason,
        [Parameter(Mandatory)] [int]    $ExitCode
    )

    Append-UpgradeLog "::warning:: [rollback] Initiating rollback: $Reason"

    # Stop new service first if it managed to come up (degraded but running).
    # Best-effort — SCM may already have force-killed it.
    try {
        $svc = Get-Service -Name $SERVICE_NAME -ErrorAction SilentlyContinue
        if ($null -ne $svc -and $svc.Status -ne 'Stopped') {
            Append-UpgradeLog "[rollback] Stopping new service (current state: $($svc.Status))"
            Stop-Service -Name $SERVICE_NAME -Force -ErrorAction SilentlyContinue
            $svc.WaitForStatus('Stopped', '00:00:30')
        }
    } catch {
        Append-UpgradeLog "[rollback] Couldn't cleanly stop new service: $($_.Exception.Message). Proceeding with restore — Move-Item might still succeed if SCM has released the binary."
    }

    # Resolve the .bak path the same way Phase B's backup step did.
    $installParent = Split-Path -Parent $INSTALL_DIR
    $installLeaf = Split-Path -Leaf $INSTALL_DIR
    $bakDir = Join-Path $installParent "$installLeaf.bak"

    if (-not (Test-Path $bakDir)) {
        Append-UpgradeLog "::error:: [rollback] No .bak directory at $bakDir — cannot auto-restore. Operator must manually reinstall."
        Write-UpgradeStatus -Status 'ROLLBACK_NEEDED' -InstallMethod $INSTALL_METHOD -Detail "Failure: $Reason. No .bak available — manual reinstall required." -ExitCode $ExitCode
        exit $ExitCode
    }

    # Move new (broken) binary aside so the restore Move-Item has a clean
    # destination. Use a `.failed` suffix so the operator can inspect the
    # broken binary post-mortem if they want.
    $failedDir = Join-Path $installParent "$installLeaf.failed"
    try {
        if (Test-Path $failedDir) {
            Remove-Item -Path $failedDir -Recurse -Force
        }
        if (Test-Path $INSTALL_DIR) {
            Append-UpgradeLog "[rollback] Moving broken $INSTALL_DIR → $failedDir for post-mortem"
            Move-Item -Path $INSTALL_DIR -Destination $failedDir -Force
        }
    } catch {
        Append-UpgradeLog "::error:: [rollback] Couldn't archive broken binary: $($_.Exception.Message). Restore may fail next step."
    }

    # Restore .bak → INSTALL_DIR.
    try {
        Append-UpgradeLog "[rollback] Restoring $bakDir → $INSTALL_DIR"
        Move-Item -Path $bakDir -Destination $INSTALL_DIR -Force
    } catch {
        Append-UpgradeLog "::error:: [rollback] Restore Move-Item failed: $($_.Exception.Message)"
        Write-UpgradeStatus -Status 'ROLLBACK_CRITICAL_FAILED' -InstallMethod $INSTALL_METHOD -Detail "Restore failed: $($_.Exception.Message). Failure: $Reason. Broken binary at $failedDir." -ExitCode $ExitCode
        exit $ExitCode
    }

    # Restart old service.
    try {
        Append-UpgradeLog "[rollback] Starting service (old binary)"
        Start-Service -Name $SERVICE_NAME

        # Wait for SCM to confirm RUNNING — synchronous proof that the old
        # service is genuinely back up before we report ROLLED_BACK.
        $svc = Get-Service -Name $SERVICE_NAME
        $svc.WaitForStatus('Running', '00:00:30')

        Append-UpgradeLog "[rollback] Service running (old binary)"
        Write-UpgradeStatus -Status 'ROLLED_BACK' -InstallMethod $INSTALL_METHOD -Detail "Rolled back from failed upgrade. Reason: $Reason. Broken binary preserved at $failedDir for inspection." -ExitCode $ExitCode
    } catch {
        Append-UpgradeLog "::error:: [rollback] Old service won't start after restore: $($_.Exception.Message)"
        Write-UpgradeStatus -Status 'ROLLBACK_CRITICAL_FAILED' -InstallMethod $INSTALL_METHOD -Detail "Restored .bak but old service won't start: $($_.Exception.Message). Original failure: $Reason." -ExitCode $ExitCode
    }

    exit $ExitCode
}

# ── Idempotency: lock file ───────────────────────────────────────────────────
# Single per-host lock (NOT per-target-version) so two concurrent operator
# clicks targeting different versions can't race the SCM Stop-Service +
# Move-Item swap + Start-Service sequence. Mirrors the Linux flock layout.
#
# J.E.7 stale-lock detection: a crashed dispatch (host reboot mid-upgrade,
# OOM kill, etc.) leaves the lock file with a dead PID. Pre-J.E.7 every
# subsequent upgrade dispatch on that host failed with exit 13 forever
# until manual lock-file deletion. Now: if the recorded PID isn't a live
# process, log + break the stale lock + proceed. A live PID still wins
# (real concurrent dispatch is correctly rejected). Pinned by
# `WindowsTentacleUpgradeStrategyTests.RenderInnerScript_StaleLockBreak_PinnedStructurally`
# + `TentacleUpgradeLifecycleE2ETests.E11u2_StaleLockWithDeadPid_BrokenAndDispatchProceeds`.
if (Test-Path $LOCK_FILE) {
    $existing = (Get-Content $LOCK_FILE -ErrorAction SilentlyContinue | Out-String).Trim()

    # Regex-parse PID. Avoids `[int]::TryParse([ref])` which has fragile
    # PowerShell binding behaviour. Empty / non-numeric / negative content
    # leaves $existingPid at 0 → falls through the > 0 guard → treated
    # as stale (correct: a non-numeric lock file is corrupt and should
    # be broken).
    $existingPid = 0
    if ($existing -match '^\d+$') {
        $existingPid = [int]$existing
    }

    $holderAlive = $false
    if ($existingPid -gt 0) {
        # Get-Process throws if PID is not a live process. -ErrorAction
        # SilentlyContinue + null check lets us distinguish "alive" (returns
        # process) from "dead" (returns $null) cleanly.
        $proc = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
        if ($null -ne $proc) {
            $holderAlive = $true
        }
    }

    if ($holderAlive) {
        Append-UpgradeLog "::error:: Upgrade lock $LOCK_FILE held by LIVE PID $existingPid — refusing concurrent upgrade dispatch"
        exit 13
    }

    # Stale lock — log + break + proceed. Operator-visible recovery from
    # a previously-crashed dispatch that left the lock orphaned.
    Append-UpgradeLog "::warning:: Upgrade lock $LOCK_FILE held by stale PID '$existing' (no live process) — breaking lock to recover from a previously-crashed dispatch"
    try { Remove-Item -Path $LOCK_FILE -Force -ErrorAction Stop } catch {
        # Couldn't break the lock (permissions / IO error). Operator must
        # intervene; surface the failure clearly.
        Append-UpgradeLog "::error:: Couldn't break stale lock at $LOCK_FILE`: $($_.Exception.Message). Operator must manually delete the lock file to recover."
        exit 13
    }
}

Set-Content -Path $LOCK_FILE -Value "$PID" -Force

try {
    Append-UpgradeLog "[upgrade] Phase A starting — target version $TARGET_VERSION on $RID"
    Write-UpgradeStatus -Status 'IN_PROGRESS' -Detail "Phase A starting (target $TARGET_VERSION)"

    # ── Already-on-target short-circuit ─────────────────────────────────────
    # If the running binary is already at the target version, short-circuit.
    # The strategy would normally do this server-side via the
    # AlreadyUpToDate path, but a stale runtime-cache could let an upgrade
    # dispatch through anyway — this is the agent-side defence-in-depth.
    $tentacleExe = Join-Path $INSTALL_DIR 'Squid.Tentacle.exe'

    if (Test-Path $tentacleExe) {
        $currentVersion = (Get-Item $tentacleExe).VersionInfo.ProductVersion

        if ($currentVersion -eq $TARGET_VERSION) {
            Append-UpgradeLog "[upgrade] Already on target version $TARGET_VERSION — no-op"
            Write-UpgradeStatus -Status 'SUCCESS' -Detail "Already on target $TARGET_VERSION (no-op)"
            exit 0
        }
    }

    # ── INSTALL_METHODS dispatch (server-injected) ──────────────────────────
    # The server replaces the placeholder below with the concatenated snippets
    # from each IWindowsUpgradeMethod's RenderDetectAndInstall.
    # ships zip-marker only;  will add chocolatey + MSI.
    #
    # IMPORTANT: do NOT mention the placeholder name verbatim anywhere except
    # the actual substitution site below. WindowsTentacleUpgradeStrategy uses
    # String.Replace which matches every occurrence — a comment-line mention
    # would be rewritten too, splicing multi-line PowerShell into a `#`-prefixed
    # line and producing parse errors that ONLY surface at agent-side Task
    # Scheduler invocation (operator sees Initiated, no upgrade actually runs).
    # Pinned by `WindowsTentacleUpgradeStrategyTests.RenderInnerScript_PlaceholderTokens_AppearExactlyOnceInTemplate`.
    $INSTALL_OK = $false
    $INSTALL_METHOD = ''

    {{INSTALL_METHODS}}

    # ── Zip-method execution block ──────────────────────────────────────────
    # The marker emitted by ZipUpgradeMethod.RenderDetectAndInstall sets
    # $INSTALL_METHOD = 'zip'. This block does the actual work.
    if ($INSTALL_METHOD -eq 'zip' -and $INSTALL_OK -ne $true) {
        $stagingDir = Join-Path $env:TEMP "squid-tentacle-staging-$([guid]::NewGuid().ToString('N'))"
        $archivePath = Join-Path $stagingDir "squid-tentacle-$TARGET_VERSION-$RID.zip"

        try {
            New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

            Append-UpgradeLog "[upgrade-method:zip] Downloading $DOWNLOAD_URL → $archivePath"

            try {
                # -UseBasicParsing avoids needing IE first-run config on
                # stripped Server Core images. -TimeoutSec 600 = 10 min budget
                # for the download (matches install-tentacle.ps1's pattern).
                Invoke-WebRequest -Uri $DOWNLOAD_URL -OutFile $archivePath -UseBasicParsing -TimeoutSec 600
            }
            catch {
                Append-UpgradeLog "[upgrade-method:zip] Download failed: $($_.Exception.Message)"
                Write-UpgradeStatus -Status 'FAILED' -InstallMethod 'zip' -Detail "Download failed: $($_.Exception.Message)" -ExitCode 2
                exit 2
            }

            # opportunistic SHA256 fetch mirroring Linux's
            # upgrade-linux-tentacle.sh:418-429 pattern. Resolution chain:
            #
            #   (1) Strategy-substituted EXPECTED_SHA256 (server-side override
            #       — operator pinned a specific hash via env var, or strategy
            #       in the future starts substituting from a manifest)
            #
            #   (2) <DOWNLOAD_URL>.sha256 companion file (has
            #       both release workflows publishing these). This is the
            #       common case once the next tag-push lands.
            #
            #   (3) Fall through with skip-with-warning (matches Linux's
            #       behaviour for older releases that don't have .sha256
            #       companions yet — backward compat for in-the-wild artifacts)
            #
            # Format expected: `sha256sum`'s default output (`<64-hex>  <filename>`).
            # We strip whitespace + tail and validate hex-only-64-chars before
            # using — anything else is treated as "no valid SHA available"
            # and falls through. Mirrors Linux's grep-and-validate guard.
            if ([string]::IsNullOrWhiteSpace($EXPECTED_SHA256)) {
                $shaUrl = "$DOWNLOAD_URL.sha256"
                Append-UpgradeLog "[upgrade-method:zip] EXPECTED_SHA256 empty — opportunistic fetch from $shaUrl"
                try {
                    $shaResponse = Invoke-WebRequest -Uri $shaUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
                    # Take ONLY the first whitespace-delimited token (hex digest).
                    # `sha256sum file > file.sha256` writes "<hex>  <filename>".
                    $fetched = ($shaResponse.Content -split '\s+', 2)[0].Trim().ToLower()
                    if ($fetched -match '^[0-9a-f]{64}$') {
                        $EXPECTED_SHA256 = $fetched
                        Append-UpgradeLog "[upgrade-method:zip] Fetched expected SHA256 from $shaUrl"
                    }
                    else {
                        Append-UpgradeLog "[upgrade-method:zip] Companion at $shaUrl returned non-hex / wrong-length content — skipping verification"
                    }
                }
                catch {
                    # Common, expected case for older releases without .sha256
                    # companions OR for air-gap mirrors that haven't replicated
                    # the companion files yet. NOT a fatal error.
                    Append-UpgradeLog "[upgrade-method:zip] No .sha256 companion at $shaUrl — skipping verification (release pipeline does not yet publish per-archive hashes for this version)"
                }
            }

            if (-not [string]::IsNullOrWhiteSpace($EXPECTED_SHA256)) {
                Append-UpgradeLog "[upgrade-method:zip] Verifying SHA256"

                # Direct .NET API for SHA256 computation (intentionally NOT the
                # PowerShell cmdlet form). The cmdlet lives in
                # `Microsoft.PowerShell.Utility` and is loaded via the module
                # auto-loader; some Windows runner images + stripped-down
                # PowerShell installations have observed the auto-loader fail
                # with `CommandNotFoundException` for the SHA cmdlet even when
                # `Invoke-WebRequest` (same module) loads fine — likely a
                # partial module-cache state under the combination of
                # `$ErrorActionPreference = 'Stop'` and
                # `Set-StrictMode -Version Latest`. Direct .NET avoids the
                # auto-loader entirely (PS 5.1's host already has the BCL
                # types loaded) AND is ~5x faster on ~10 MB archives. Same
                # digest, more portable.
                # Pinned by `WindowsTentacleUpgradeStrategyTests.RenderInnerScript_ShaVerifyUsesDirectDotNetApi_NotGetFileHashCmdlet`.
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $bytes = [System.IO.File]::ReadAllBytes($archivePath)
                    $hashBytes = $sha256.ComputeHash($bytes)
                    $actualSha = ([System.BitConverter]::ToString($hashBytes) -replace '-', '').ToLower()
                }
                finally {
                    $sha256.Dispose()
                }
                $expectedSha = $EXPECTED_SHA256.ToLower()

                if ($actualSha -ne $expectedSha) {
                    Append-UpgradeLog "[upgrade-method:zip] SHA256 mismatch: expected $expectedSha, got $actualSha"
                    Write-UpgradeStatus -Status 'FAILED' -InstallMethod 'zip' -Detail "SHA256 mismatch (expected $expectedSha, got $actualSha)" -ExitCode 7
                    exit 7
                }
                Append-UpgradeLog "[upgrade-method:zip] SHA256 verified: $actualSha"
            }

            $extractDir = Join-Path $stagingDir 'extract'
            New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

            Append-UpgradeLog "[upgrade-method:zip] Extracting to $extractDir"
            Expand-Archive -Path $archivePath -DestinationPath $extractDir -Force

            $extractedExe = Join-Path $extractDir 'Squid.Tentacle.exe'

            if (-not (Test-Path $extractedExe)) {
                Append-UpgradeLog "[upgrade-method:zip] Extracted archive missing Squid.Tentacle.exe"
                Write-UpgradeStatus -Status 'FAILED' -InstallMethod 'zip' -Detail "Extracted archive missing Squid.Tentacle.exe" -ExitCode 3
                exit 3
            }

            $INSTALL_OK = $true
            Append-UpgradeLog "[upgrade-method:zip] Phase A complete — staging dir $extractDir ready for swap"
        }
        catch {
            Append-UpgradeLog "[upgrade-method:zip] Unexpected failure: $($_.Exception.Message)"
            Write-UpgradeStatus -Status 'FAILED' -InstallMethod 'zip' -Detail "Unexpected failure: $($_.Exception.Message)" -ExitCode 14
            exit 14
        }
    }

    # ── No method matched — operator must investigate ───────────────────────
    if ($INSTALL_OK -ne $true) {
        Append-UpgradeLog "::error:: No install method succeeded — INSTALL_METHOD='$INSTALL_METHOD'"
        Write-UpgradeStatus -Status 'FAILED' -InstallMethod $INSTALL_METHOD -Detail "No install method succeeded" -ExitCode 14
        exit 14
    }

    # ── Phase B: Stop service → swap → start service → health check ─────────
    # This block runs SYNCHRONOUSLY and assumes the strategy has
    # already detached the script process from the Tentacle service tree
    # (likely via a Task Scheduler one-shot wrapper, equivalent to Linux's
    # `systemd-run --scope`). If the script is invoked directly inside the
    # Tentacle process tree without a detach wrapper, Stop-Service will
    # terminate this process before Phase B completes — an orchestration
    # concern owned by E.3's strategy, not by this template.
    Append-UpgradeLog "[upgrade] Phase B starting — stopping service '$SERVICE_NAME' and swapping binary"

    $serviceWasRunning = $false
    $svc = Get-Service -Name $SERVICE_NAME -ErrorAction SilentlyContinue

    if ($null -ne $svc) {
        $serviceWasRunning = ($svc.Status -eq 'Running')

        if ($serviceWasRunning) {
            Append-UpgradeLog "[upgrade] Stopping service $SERVICE_NAME"
            Stop-Service -Name $SERVICE_NAME -Force

            # Wait up to 30s for the service to actually stop (Stop-Service
            # returns when the SCM accepts the stop command; the service
            # process may take a moment longer to exit).
            $svc.WaitForStatus('Stopped', '00:00:30')
        }
    }
    else {
        Append-UpgradeLog "[upgrade] Service $SERVICE_NAME not registered yet — first install path"
    }

    # Backup current install (zip method requires explicit swap)
    if ($INSTALL_METHOD -eq 'zip') {
        # Robust .bak sibling path: Split-Path normalises trailing separators
        # so a future operator setting INSTALL_DIR with a trailing backslash
        # ("C:\Squid\") doesn't produce a hidden ".bak" leaf inside the dir.
        # Linux side doesn't have this concern (no spaces, no trailing-slash
        # convention drift) — Windows ProgramFiles paths often round-trip
        # through clipboards/CLIs that re-add separators, so guard for it.
        $installParent = Split-Path -Parent $INSTALL_DIR
        $installLeaf = Split-Path -Leaf $INSTALL_DIR
        $bakDir = Join-Path $installParent "$installLeaf.bak"

        if (Test-Path $bakDir) {
            Remove-Item -Path $bakDir -Recurse -Force
        }

        if (Test-Path $INSTALL_DIR) {
            Append-UpgradeLog "[upgrade] Backing up $INSTALL_DIR → $bakDir"
            Move-Item -Path $INSTALL_DIR -Destination $bakDir -Force
        }

        # $extractDir was set in Phase A's zip block at the same scope.
        # Using the variable directly (vs. a Get-ChildItem LastWriteTime
        # search) avoids a race when two operators dispatch concurrent
        # upgrades to different versions: the earlier dispatch's staging
        # dir lingers in %TEMP% until Phase B cleanup, and a LastWriteTime
        # winner could be the wrong version. Phase A's per-host lock at
        # line ~145 already prevents the truly concurrent case, but reading
        # the in-scope variable removes any remaining ambiguity.
        if (-not (Test-Path $extractDir)) {
            Append-UpgradeLog "::error:: Phase B can't find Phase A staging dir at expected path: $extractDir"
            Write-UpgradeStatus -Status 'ROLLBACK_CRITICAL_FAILED' -InstallMethod 'zip' -Detail "Staging dir disappeared between phases: $extractDir" -ExitCode 14
            exit 14
        }

        Append-UpgradeLog "[upgrade] Moving $extractDir → $INSTALL_DIR"
        Move-Item -Path $extractDir -Destination $INSTALL_DIR -Force

        Write-UpgradeStatus -Status 'SWAPPED' -InstallMethod 'zip' -Detail "Binary swapped — restarting service"
    }

    # Start the service (may already be running for first-install path).
    # Wrap in try/catch — if the new binary's OnStart throws (e.g. SCM 1067
    # "service did not start in a timely fashion"), call Invoke-Rollback so
    # the agent recovers to the old binary instead of being left in a half-
    # swapped Stopped state. Wait for RUNNING state too — Start-Service
    # returns when SCM accepts the start command, but the actual process
    # OnStart may still throw a few seconds later. WaitForStatus surfaces
    # that synchronously so the catch is reachable.
    if ($null -ne (Get-Service -Name $SERVICE_NAME -ErrorAction SilentlyContinue)) {
        Append-UpgradeLog "[upgrade] Starting service $SERVICE_NAME"
        try {
            Start-Service -Name $SERVICE_NAME

            $svcVerify = Get-Service -Name $SERVICE_NAME
            $svcVerify.WaitForStatus('Running', '00:00:30')

            Append-UpgradeLog "[upgrade] Service $SERVICE_NAME reached RUNNING state"
        } catch {
            # Auto-rollback to old binary. Reason is captured for operator
            # diagnostics in last-upgrade.json + upgrade.log. Exit 8 is the
            # documented "Start-Service post-swap failed" code.
            Invoke-Rollback -Reason "Start-Service post-swap failed: $($_.Exception.Message)" -ExitCode 8
        }
    }

    # ── Health check ────────────────────────────────────────────────────────
    # Retry count is server-substituted per dispatch (default 30 attempts ×
    # 2s sleep = 60s wait window, see WindowsTentacleUpgradeStrategy.DefaultHealthcheckRetries).
    # Operator override via SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_RETRIES
    # env var on the SERVER side: deployments with slow-starting agents
    # (heavy plugin enumeration, >60s) set this to 90 (3 min) so the wait
    # is realistic for their environment without changing default behaviour.
    # Tests set retries=1 to bypass the wait entirely (the test service
    # doesn't expose HTTP, so every poll attempt 404s and the wait is pure
    # cost). Pinned by `WindowsTentacleUpgradeStrategyTests.HealthcheckRetriesEnvVar_*`.
    $totalWaitSeconds = $HEALTHCHECK_RETRIES * 2
    Append-UpgradeLog "[upgrade] Waiting for healthcheck $HEALTHCHECK_URL (max $HEALTHCHECK_RETRIES attempts × 2s = ${totalWaitSeconds}s)"

    $healthOk = $false

    for ($i = 0; $i -lt $HEALTHCHECK_RETRIES; $i++) {
        Start-Sleep -Seconds 2

        try {
            $resp = Invoke-WebRequest -Uri $HEALTHCHECK_URL -UseBasicParsing -TimeoutSec 5

            if ($resp.StatusCode -eq 200) {
                $healthOk = $true
                break
            }
        }
        catch {
            # Expected during the restart window — keep polling
        }
    }

    if (-not $healthOk) {
        if ($HEALTHCHECK_FATAL) {
            # Strict mode (J.E.7): operator opted in to treat healthcheck
            # timeout as a deal-breaker. Roll back to the previous binary
            # rather than leave the operator with a Stopped+swapped service
            # that the next capabilities probe will report as broken anyway.
            # Exit 9 is the documented "healthcheck timeout (FATAL mode)
            # → rollback fired" code.
            Invoke-Rollback -Reason "Healthcheck didn't respond within ${totalWaitSeconds}s and SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_FATAL=true (strict mode)" -ExitCode 9
        }

        # Default mode (matches Octopus Tentacle): warning + proceed.
        # Capabilities probe will detect a dead agent on the next probe.
        Append-UpgradeLog "::warning:: Healthcheck didn't respond within ${totalWaitSeconds}s — proceeding anyway, server will retry on next health probe (set SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_FATAL=true to roll back instead)"
    }

    Write-UpgradeStatus -Status 'SUCCESS' -InstallMethod $INSTALL_METHOD -Detail "Upgrade to $TARGET_VERSION complete"
    Append-UpgradeLog "[upgrade] Phase B complete — version $TARGET_VERSION installed via $INSTALL_METHOD"
    exit 0
}
finally {
    Remove-Item -Path $LOCK_FILE -Force -ErrorAction SilentlyContinue
}
