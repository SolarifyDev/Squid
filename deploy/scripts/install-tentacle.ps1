<#
.SYNOPSIS
    Squid Tentacle Windows installer.

.DESCRIPTION
    Downloads a self-contained Squid Tentacle .zip from GitHub Releases,
    extracts it to the install dir, registers a Windows Firewall inbound
    rule, and (unless `-NoServiceInstall`) installs the Tentacle as a
    Windows Service via `Squid.Tentacle.exe service install`.

    Compatibility goals (every Windows scenario must work):
      * Auto-elevates via UAC when not run as Administrator
      * Works in both file-invocation (`.\install-tentacle.ps1`) AND
        pipe-invocation (`irm <url> | iex`) modes
      * Writes a discovery file at
        `%ProgramData%\Squid\Tentacle\install-info.json` so downstream
        scripts (register, service install, upgrade) find the binary
        regardless of where the operator installed it
      * Honours custom `INSTALL_DIR` (env var) or `-InstallDir` overrides

.PARAMETER Version
    Tentacle version to install (e.g. 1.6.0). Defaults to "latest". Override via
    env var TENTACLE_VERSION.

.PARAMETER InstallDir
    Where to extract the binaries. Defaults to
    "C:\Program Files\Squid Tentacle". Override via env var INSTALL_DIR.

.PARAMETER DownloadBase
    Base URL for the GitHub Releases. Override via env var DOWNLOAD_BASE for
    air-gapped operators pointing at a private mirror.

.PARAMETER ServiceName
    Windows service name. Defaults to "squid-tentacle".

.PARAMETER NoServiceInstall
    Skip the `service install` step. Use when bootstrapping a base VM image
    that will be cloned -- the SCM-registered service identity must be unique
    per machine.

.PARAMETER NoAutoElevate
    Skip the UAC auto-elevation. Use when the caller knows the current shell
    is already elevated (avoids a redundant re-launch) OR when running
    non-interactively (CI, scheduled task as SYSTEM) where the UAC prompt
    is undesired or impossible.

.EXAMPLE
    # One-liner (latest) -- auto-elevates if needed
    irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 | iex

.EXAMPLE
    # Specific version with custom install dir
    .\install-tentacle.ps1 -Version 1.6.9 -InstallDir "D:\squid-tentacle"

.EXAMPLE
    # Bootstrap-time install (no service yet)
    .\install-tentacle.ps1 -NoServiceInstall

.NOTES
    Discovery file schema (Schema = 1):
        BinaryPath, InstallDir, Version, Architecture, InstalledAt,
        InstalledBy, ServiceName
    Downstream scripts (Squid-generated register/service-install snippet)
    read this file to locate the binary.
#>
[CmdletBinding()]
param(
    [string] $Version = $env:TENTACLE_VERSION,
    [string] $InstallDir = $env:INSTALL_DIR,
    [string] $DownloadBase = $env:DOWNLOAD_BASE,
    [string] $ServiceName = 'squid-tentacle',
    [switch] $NoServiceInstall,
    [switch] $NoAutoElevate
)

$ErrorActionPreference = 'Stop'

# -- Defaults (Rule 8 -- pinned literals) -------------------------------------
# Renaming any of these breaks operator runbooks, MDM playbooks, and the
# in-UI script-generator's expectations.
$DefaultVersion       = 'latest'
$DefaultInstallDir    = 'C:\Program Files\Squid Tentacle'
$DefaultDownloadBase  = 'https://github.com/SolarifyDev/Squid/releases'
$BinaryName           = 'Squid.Tentacle.exe'
$ListeningPort        = 10933

# Discovery file path -- Server-generated `register` / `service install`
# snippet reads this to locate the binary regardless of install dir.
$InstallInfoDir       = Join-Path $env:ProgramData 'Squid\Tentacle'
$InstallInfoPath      = Join-Path $InstallInfoDir 'install-info.json'
$InstallInfoSchema    = 1

if ([string]::IsNullOrWhiteSpace($Version))      { $Version = $DefaultVersion }
if ([string]::IsNullOrWhiteSpace($InstallDir))   { $InstallDir = $DefaultInstallDir }
if ([string]::IsNullOrWhiteSpace($DownloadBase)) { $DownloadBase = $DefaultDownloadBase }

# -- Arch detection ----------------------------------------------------------
function Resolve-Rid {
    param([string] $Arch)

    switch ($Arch.ToUpperInvariant()) {
        'AMD64' { return 'win-x64' }
        'ARM64' { return 'win-arm64' }
        default {
            throw "Unsupported architecture: $Arch. Squid Tentacle ships for win-x64 and win-arm64 only -- 32-bit Windows is not supported."
        }
    }
}

$arch = $env:PROCESSOR_ARCHITECTURE
if ([string]::IsNullOrWhiteSpace($arch)) {
    throw "PROCESSOR_ARCHITECTURE env var is not set. This script must run on Windows."
}

$rid = Resolve-Rid -Arch $arch

# -- UAC auto-elevation ------------------------------------------------------
# Default install dir (%ProgramFiles%) + writing %ProgramData%\Squid both
# require Administrator. Rather than refuse to run for non-admin operators,
# we re-launch this script under UAC.
#
# Two invocation modes need handling:
#   1. File-invocation  (`.\install-tentacle.ps1 ...`) -- $PSCommandPath is set;
#                       we relaunch the same file.
#   2. Pipe-invocation  (`irm <url> | iex`) -- $PSCommandPath is empty; the
#                       script body lives in $MyInvocation.MyCommand.ScriptContents.
#                       We materialise it to %TEMP%\squid-install-{guid}.ps1
#                       and relaunch that.
#
# In both cases the original (non-admin) process exits with code 0 after
# the elevated child finishes -- operator sees the UAC prompt, then progress.
#
# Skip elevation when:
#   - Already admin (Test-IsAdministrator)
#   - Installing to a user-owned path (InstallDir != default) -- admin not required
#   - -NoAutoElevate switch was passed (CI, SYSTEM-context invocations)
# Defined only when no ambient definition exists, so a test harness can inject a
# mock via a parent-scope `function global:Test-IsAdministrator`. A plain
# script-local `function` would shadow any global override (PowerShell resolves
# the script-local definition first), so the mock would never fire. Normal
# installs have no ambient definition and use the real principal check below.
if (-not (Test-Path Function:\Test-IsAdministrator)) {
    function Test-IsAdministrator {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    }
}

# The UAC re-launch itself, factored into an overridable seam (same guard
# rationale as Test-IsAdministrator). A test harness overrides this via a
# parent-scope `function global:Invoke-UacRelaunch` to capture the invocation
# instead of spawning a real elevated process -- mocking the built-in
# Start-Process cmdlet is unreliable for the -Verb RunAs / ShellExecute path
# (the returned process often doesn't expose ExitCode). Returns the child exit code.
if (-not (Test-Path Function:\Invoke-UacRelaunch)) {
    function Invoke-UacRelaunch {
        param([string] $FilePath, [string] $Verb, [string[]] $ArgumentList)
        $proc = Start-Process -FilePath $FilePath -Verb $Verb -ArgumentList $ArgumentList -Wait -PassThru
        return $proc.ExitCode
    }
}

function Get-OriginalArgs {
    # Reconstruct the user's original args so the elevated re-launch sees
    # exactly the same parameters. Only emit explicitly-bound parameters
    # (defaults are re-applied by the relaunched process).
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        $name = $entry.Key
        $value = $entry.Value
        if ($value -is [System.Management.Automation.SwitchParameter]) {
            if ($value.IsPresent) { $list.Add("-$name") }
        } else {
            $list.Add("-$name")
            $list.Add("`"$value`"")
        }
    }
    return ,$list.ToArray()
}

function Invoke-SelfElevation {
    # Stage 1: locate (or materialise) the script body
    $scriptPath = $PSCommandPath
    $isPipeInvocation = [string]::IsNullOrWhiteSpace($scriptPath)

    if ($isPipeInvocation) {
        $scriptPath = Join-Path $env:TEMP ("squid-install-" + [guid]::NewGuid().ToString('N') + ".ps1")
        $body = $MyInvocation.MyCommand.ScriptContents
        if ([string]::IsNullOrWhiteSpace($body)) {
            throw "Auto-elevation impossible: invoked via pipe but `$MyInvocation.MyCommand.ScriptContents` is empty. Run from an elevated PowerShell directly, or download the script to disk first."
        }
        # Write WITHOUT BOM -- PowerShell.exe (5.1) accepts UTF-8 with or without
        # BOM but staying BOM-less keeps `Get-Content` of the file identical to
        # the original irm response.
        [System.IO.File]::WriteAllText($scriptPath, $body, [System.Text.UTF8Encoding]::new($false))
    }

    # Stage 2: assemble argv for the elevated PowerShell
    $forwarded = Get-OriginalArgs
    $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) + $forwarded

    Write-Host ""
    Write-Host "Administrator privileges required. Triggering UAC re-launch..."
    Write-Host ""

    try {
        $childExit = Invoke-UacRelaunch -FilePath 'powershell.exe' -Verb 'RunAs' -ArgumentList $childArgs
    } catch {
        Write-Error @"
UAC elevation failed: $($_.Exception.Message)

Workarounds:
  1. Right-click PowerShell -> 'Run as Administrator', then re-run this script
  2. From an admin terminal: powershell -NoProfile -ExecutionPolicy Bypass -File '$scriptPath' $(($forwarded -join ' '))
  3. Install to a user-owned path (no admin needed):
       .\install-tentacle.ps1 -InstallDir "$env:USERPROFILE\squid-tentacle"
"@
        exit 1
    } finally {
        if ($isPipeInvocation -and (Test-Path -LiteralPath $scriptPath)) {
            Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
        }
    }

    exit $childExit
}

$needsElevation = ($InstallDir -eq $DefaultInstallDir) -and -not (Test-IsAdministrator)
if ($needsElevation -and -not $NoAutoElevate) {
    Invoke-SelfElevation
}

if ($needsElevation -and $NoAutoElevate) {
    Write-Error @"
Administrator privileges required for the default install dir ($DefaultInstallDir), but -NoAutoElevate was set.

Either:
  1. Drop -NoAutoElevate to allow UAC re-launch
  2. Re-run from an already-elevated PowerShell
  3. Install to a user-owned path:
       .\install-tentacle.ps1 -InstallDir "$env:USERPROFILE\squid-tentacle"
"@
    exit 1
}

Write-Host '=== Squid Tentacle Installer ==='
Write-Host "Version:  $Version"
Write-Host "Arch:     $rid"
Write-Host "Install:  $InstallDir"
Write-Host ''

# -- Download URL resolution -------------------------------------------------
function Resolve-DownloadUrls {
    param(
        [string] $Version,
        [string] $Rid,
        [string] $DownloadBase
    )

    if ($Version -eq 'latest') {
        return @("$DownloadBase/latest/download/squid-tentacle-$Rid.zip")
    }

    return @(
        "$DownloadBase/download/$Version/squid-tentacle-$Version-$Rid.zip",
        "$DownloadBase/download/v$Version/squid-tentacle-$Version-$Rid.zip"
    )
}

# -- Generic retry helper -- retry transient failures (network blips, Defender
# file locks) with linear backoff so a single hiccup doesn't fail the install.
# Re-throws once attempts are exhausted so the caller's catch handles it.
function Invoke-WithRetry {
    param(
        [scriptblock] $Action,
        [string] $Label,
        [int] $MaxAttempts = 3,
        [int] $BaseDelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            & $Action
            return
        }
        catch {
            if ($attempt -ge $MaxAttempts) { throw }

            $delay = $BaseDelaySeconds * $attempt
            Write-Host "  $Label attempt $attempt/$MaxAttempts failed: $($_.Exception.Message) -- retrying in ${delay}s"
            Start-Sleep -Seconds $delay
        }
    }
}

$urls = Resolve-DownloadUrls -Version $Version -Rid $rid -DownloadBase $DownloadBase

$tempDir = Join-Path $env:TEMP "squid-tentacle-install-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    $archivePath = Join-Path $tempDir 'tentacle.zip'

    $downloaded = $false
    foreach ($url in $urls) {
        Write-Host "Downloading from $url..."

        try {
            # System.Net.WebClient + forced TLS 1.2, NOT Invoke-WebRequest: PS 5.1's
            # Invoke-WebRequest -OutFile corrupts large binary downloads on some hosts
            # (truncated zip -> extraction "End of central directory not found"); WebClient
            # streams the bytes verbatim. Same fix as upgrade-windows-tentacle.ps1.
            [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12

            # Retry each URL a few times before falling through to the next candidate,
            # and route through the configured proxy with default creds (407 on
            # authenticated corporate proxies).
            Invoke-WithRetry -Label "download" -Action {
                if (Test-Path $archivePath) { Remove-Item -Path $archivePath -Force -ErrorAction SilentlyContinue }

                $wc = New-Object System.Net.WebClient
                $wc.UseDefaultCredentials = $true
                if ($wc.Proxy) { $wc.Proxy.Credentials = [System.Net.CredentialCache]::DefaultNetworkCredentials }
                try { $wc.DownloadFile($url, $archivePath) } finally { $wc.Dispose() }
            }

            # Reject a 0-byte / truncated file (error page returned with HTTP 200) so it
            # fails here with a clear message instead of an opaque extraction error.
            $archiveSize = (Get-Item $archivePath).Length
            if ($archiveSize -lt 1024) {
                throw "Downloaded archive is only $archiveSize byte(s) -- expected a multi-MB zip (likely an error page returned with HTTP 200)."
            }

            $downloaded = $true
            break
        } catch {
            Write-Host "  Failed: $($_.Exception.Message). Trying next URL (if any)..."
        }
    }

    if (-not $downloaded) {
        Write-Error @"
Could not download squid-tentacle-$rid.zip from any of:
$($urls -join "`n")

Possible causes:
  1. Tag '$Version' does not exist on GitHub Releases
  2. Outbound HTTPS to github.com blocked by firewall/proxy
  3. For air-gapped installs, set DOWNLOAD_BASE to a private mirror
"@
        exit 1
    }

    # -- Extract into a versioned ("blue-green") layout -----------------------
    # Binary lives in versions\<v>; a stable `current` junction selects the active
    # version. A later upgrade repoints `current` without touching the running
    # version's directory, so any failure leaves the old version intact. Stage
    # first, then name the dir by the concrete version the binary reports (works
    # for both `latest` and an explicit -Version). Junctions need no elevation.
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    $stagingDir = Join-Path $tempDir 'extract'

    Write-Host "Extracting..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    # Retry extraction: Defender can briefly lock a freshly-written file. Clear any
    # partial output first since ExtractToDirectory has no overwrite overload on PS 5.1.
    Invoke-WithRetry -Label "extraction" -Action {
        if (Test-Path $stagingDir) { Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue }
        New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $stagingDir)
    }

    $stagedBinary = Join-Path $stagingDir $BinaryName
    if (-not (Test-Path $stagedBinary)) {
        Write-Error "Extraction completed but $BinaryName is missing. The archive may be corrupt."
        exit 1
    }

    $resolvedVersion = ''
    try {
        $verOut = & $stagedBinary version 2>$null | Select-Object -First 1
        if ($verOut) { $resolvedVersion = ($verOut -replace '[^0-9A-Za-z._-]', '') }
    } catch { }

    if ($resolvedVersion) {
        $versionsRoot = Join-Path $InstallDir 'versions'
        $versionDir = Join-Path $versionsRoot $resolvedVersion
        if (-not (Test-Path $versionsRoot)) { New-Item -ItemType Directory -Path $versionsRoot -Force | Out-Null }
        if (Test-Path $versionDir) { Remove-Item -Path $versionDir -Recurse -Force }
        # Retry the swap: Defender may briefly lock a freshly-extracted file.
        Invoke-WithRetry -Label "version swap" -Action { Move-Item -Path $stagingDir -Destination $versionDir }

        # Repoint `current` at the new version. Delete the old junction
        # NON-recursively ([Directory]::Delete(path, $false)) so we remove only the
        # reparse point, never the target version's files.
        $currentPath = Join-Path $InstallDir 'current'
        if (Test-Path $currentPath) { [System.IO.Directory]::Delete($currentPath, $false) }
        New-Item -ItemType Junction -Path $currentPath -Target $versionDir | Out-Null

        $binaryPath = Join-Path $currentPath $BinaryName
        Write-Host "Installed versioned layout: current -> versions\$resolvedVersion"
    } else {
        # Best-effort fallback: the binary couldn't report its version. Extract flat
        # so the install completes exactly as it did before the versioned layout
        # existed. Never fail an install that used to succeed.
        Write-Host "Warning: binary did not report a version; using flat layout (no versioned upgrades)."
        Copy-Item -Path (Join-Path $stagingDir '*') -Destination $InstallDir -Recurse -Force
        $binaryPath = Join-Path $InstallDir $BinaryName
    }

    if (-not (Test-Path $binaryPath)) {
        Write-Error "Install completed but $BinaryName is missing at $binaryPath. The archive may be corrupt."
        exit 1
    }

    Write-Host "Installed: $binaryPath"
} finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# -- Discovery file ----------------------------------------------------------
# Downstream scripts read %ProgramData%\Squid\Tentacle\install-info.json to
# locate the binary. Without this, the server-generated register/service-install
# snippet would have to hardcode the install path -- breaking custom InstallDir.
function Write-InstallInfo {
    param(
        [string] $BinaryPath,
        [string] $InstallDir,
        [string] $Version,
        [string] $Rid,
        [string] $ServiceName,
        [string] $InfoDir,
        [string] $InfoPath,
        [int]    $Schema
    )

    if (-not (Test-Path $InfoDir)) {
        New-Item -ItemType Directory -Path $InfoDir -Force | Out-Null
    }

    $info = [ordered]@{
        Schema        = $Schema
        BinaryPath    = $BinaryPath
        InstallDir    = $InstallDir
        Version       = $Version
        Architecture  = $Rid
        InstalledAt   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        InstalledBy   = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        ServiceName   = $ServiceName
    }

    $info | ConvertTo-Json -Depth 3 | Set-Content -Path $InfoPath -Encoding UTF8
    Write-Host "Install info: $InfoPath"
}

Write-InstallInfo `
    -BinaryPath $binaryPath `
    -InstallDir $InstallDir `
    -Version $Version `
    -Rid $rid `
    -ServiceName $ServiceName `
    -InfoDir $InstallInfoDir `
    -InfoPath $InstallInfoPath `
    -Schema $InstallInfoSchema

# -- Firewall rule for Listening Tentacle ------------------------------------
function Add-ListeningFirewallRule {
    param(
        [string] $RuleName,
        [int]    $Port
    )

    $existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue

    if ($existing) {
        Write-Host "Firewall rule '$RuleName' already exists -- skipping."
        return
    }

    New-NetFirewallRule `
        -DisplayName $RuleName `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $Port `
        -Profile Any | Out-Null

    Write-Host "Created firewall rule '$RuleName' (TCP/$Port inbound)."
}

try {
    Add-ListeningFirewallRule -RuleName "Squid Tentacle (Listening)" -Port $ListeningPort
} catch {
    Write-Warning "Failed to add firewall rule: $($_.Exception.Message). For Listening Tentacle, manually run: New-NetFirewallRule -DisplayName 'Squid Tentacle' -Direction Inbound -Protocol TCP -LocalPort $ListeningPort -Action Allow"
}

# -- Service install ---------------------------------------------------------
if ($NoServiceInstall) {
    Write-Host ''
    Write-Host '-NoServiceInstall set -- skipping service install.'
    Write-Host "To install the service later, run:"
    Write-Host "  & '$binaryPath' service install --instance Default --service-name $ServiceName"
    Write-Host ''
} else {
    Write-Host ''
    Write-Host "Installing Windows service '$ServiceName'..."

    & $binaryPath service install --instance Default --service-name $ServiceName

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Service install failed (exit code $LASTEXITCODE). Check the output above for details."
        exit $LASTEXITCODE
    }

    Write-Host "Service '$ServiceName' installed and started."
    Write-Host ''
}

# -- Next-step hints ---------------------------------------------------------
Write-Host '=== Next steps ==='
Write-Host ''
Write-Host 'Register the agent with your Squid server (the install-info.json above'
Write-Host 'lets the Squid Web UI generate a copy-pasteable register snippet that'
Write-Host 'discovers the binary automatically -- no hardcoded paths):'
Write-Host ''
Write-Host '  Listening agent (Squid server connects IN to this Windows host):'
Write-Host "      & '$binaryPath' register \"
Write-Host "          --server https://YOUR_SQUID_SERVER \"
Write-Host "          --api-key API-YOUR_API_KEY \"
Write-Host "          --role web-server \"
Write-Host "          --environment Production"
Write-Host ''
Write-Host '  Polling agent (this host dials OUT to Squid through a firewall):'
Write-Host "      & '$binaryPath' register \"
Write-Host "          --server https://YOUR_SQUID_SERVER \"
Write-Host "          --api-key API-YOUR_API_KEY \"
Write-Host "          --comms-url https://YOUR_SQUID_SERVER:10943 \"
Write-Host "          --role web-server \"
Write-Host "          --environment Production"
Write-Host ''
Write-Host 'Service status:'
Write-Host "  sc query $ServiceName"
Write-Host ''
