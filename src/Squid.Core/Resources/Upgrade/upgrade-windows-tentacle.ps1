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

# ── Idempotency: lock file ───────────────────────────────────────────────────
# Single per-host lock (NOT per-target-version) so two concurrent operator
# clicks targeting different versions can't race the SCM Stop-Service +
# Move-Item swap + Start-Service sequence. Mirrors the Linux flock layout.
if (Test-Path $LOCK_FILE) {
    $existing = Get-Content $LOCK_FILE -ErrorAction SilentlyContinue
    Append-UpgradeLog "::error:: Upgrade lock $LOCK_FILE already held (held by PID $existing) — refusing concurrent upgrade dispatch"
    exit 13
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
    # The server replaces {{INSTALL_METHODS}} with the concatenated snippets
    # from each IWindowsUpgradeMethod's RenderDetectAndInstall.
    # ships zip-marker only;  will add chocolatey + MSI.
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

                $actualSha = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLower()
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

    # Start the service (may already be running for first-install path)
    if ($null -ne (Get-Service -Name $SERVICE_NAME -ErrorAction SilentlyContinue)) {
        Append-UpgradeLog "[upgrade] Starting service $SERVICE_NAME"
        Start-Service -Name $SERVICE_NAME
    }

    # ── Health check ────────────────────────────────────────────────────────
    Append-UpgradeLog "[upgrade] Waiting for healthcheck $HEALTHCHECK_URL"

    $healthOk = $false

    for ($i = 0; $i -lt 30; $i++) {
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
        Append-UpgradeLog "::warning:: Healthcheck didn't respond within 60s — proceeding anyway, server will retry on next health probe"
    }

    Write-UpgradeStatus -Status 'SUCCESS' -InstallMethod $INSTALL_METHOD -Detail "Upgrade to $TARGET_VERSION complete"
    Append-UpgradeLog "[upgrade] Phase B complete — version $TARGET_VERSION installed via $INSTALL_METHOD"
    exit 0
}
finally {
    Remove-Item -Path $LOCK_FILE -Force -ErrorAction SilentlyContinue
}
