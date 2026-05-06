<#
.SYNOPSIS
    Squid Tentacle Windows installer.

.DESCRIPTION
    Downloads a self-contained Squid Tentacle .zip from GitHub Releases,
    extracts it to the install dir, and registers it as a Windows service
    via `squid-tentacle service install` (Phase-12.C / sc.exe under the hood).

    Companion to deploy/scripts/install-tentacle.sh - same one-liner UX,
    same arg shape (--version maps to -Version), same env-var override
    points (TENTACLE_VERSION → -Version, INSTALL_DIR → -InstallDir,
    DOWNLOAD_BASE → -DownloadBase). Per Phase-12.E analysis, Octopus's
    .NET-Framework MSI flow does not 1:1 to a .NET 9 self-contained app:
    Squid ships zip + this script, the zip is extracted by Expand-Archive
    instead of msiexec, and service install runs through the same CLI
    surface the in-UI upgrade flow will eventually reuse.

.PARAMETER Version
    Tentacle version to install (e.g. 1.6.0). Defaults to "latest" - uses
    GitHub's /releases/latest/download redirect. Override via env var
    TENTACLE_VERSION.

.PARAMETER InstallDir
    Where to extract the binaries. Defaults to
    "C:\Program Files\Squid Tentacle". Override via env var INSTALL_DIR.

.PARAMETER DownloadBase
    Base URL for the GitHub Releases. Defaults to
    "https://github.com/SolarifyDev/Squid/releases". Override via env var
    DOWNLOAD_BASE - useful for air-gapped operators pointing at a private
    mirror that copies the GitHub release tree.

.PARAMETER ServiceName
    Windows service name. Defaults to "squid-tentacle" (mirrors the Linux
    systemd unit name). Pinned per Rule 8 - operator runbooks reference
    this literal.

.PARAMETER NoServiceInstall
    Skip the `squid-tentacle service install` step. Use when bootstrapping
    a base VM image that will be cloned - the service install MUST happen
    AFTER the image is cloned, otherwise every clone shares the same
    SCM-registered service identity. Same caveat as Octopus's "do not
    complete the configuration wizard before snapshotting".

.EXAMPLE
    # One-liner (latest)
    irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 | iex

.EXAMPLE
    # Specific version
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1))) -Version 1.6.0

.EXAMPLE
    # Bootstrap-time install (no service yet, registers later)
    .\install-tentacle.ps1 -NoServiceInstall

.NOTES
    Author: Squid project
    Phase:  P1-Phase12.E.0
#>
[CmdletBinding()]
param(
    [string] $Version = $env:TENTACLE_VERSION,
    [string] $InstallDir = $env:INSTALL_DIR,
    [string] $DownloadBase = $env:DOWNLOAD_BASE,
    [string] $ServiceName = 'squid-tentacle',
    [switch] $NoServiceInstall
)

$ErrorActionPreference = 'Stop'

# -- Defaults (Rule 8 - pinned literals) --------------------------------------
# Renaming any of these breaks operator runbooks, MDM playbooks, and the
# in-UI upgrade flow's expectations.
$DefaultVersion       = 'latest'
$DefaultInstallDir    = 'C:\Program Files\Squid Tentacle'
$DefaultDownloadBase  = 'https://github.com/SolarifyDev/Squid/releases'
$BinaryName           = 'Squid.Tentacle.exe'
$ListeningPort        = 10933

if ([string]::IsNullOrWhiteSpace($Version))      { $Version = $DefaultVersion }
if ([string]::IsNullOrWhiteSpace($InstallDir))   { $InstallDir = $DefaultInstallDir }
if ([string]::IsNullOrWhiteSpace($DownloadBase)) { $DownloadBase = $DefaultDownloadBase }

# -- Arch detection ----------------------------------------------------------
# $env:PROCESSOR_ARCHITECTURE on a 64-bit OS is "AMD64" (x64) or "ARM64".
# 32-bit Windows is intentionally NOT supported - Phase-12.E analysis
# (per Octopus docs review) concluded x86 has no real audience in 2026.
function Resolve-Rid {
    param([string] $Arch)

    switch ($Arch.ToUpperInvariant()) {
        'AMD64' { return 'win-x64' }
        'ARM64' { return 'win-arm64' }
        default {
            throw "Unsupported architecture: $Arch. Squid Tentacle ships for win-x64 and win-arm64 only - 32-bit Windows is not supported."
        }
    }
}

$arch = $env:PROCESSOR_ARCHITECTURE
if ([string]::IsNullOrWhiteSpace($arch)) {
    # PowerShell on non-Windows doesn't set this. Help local developers
    # who curiously curl the script on macOS/Linux see a clean error
    # rather than a "Unsupported: " message.
    throw "PROCESSOR_ARCHITECTURE env var is not set. This script must run on Windows."
}

$rid = Resolve-Rid -Arch $arch

# -- Elevation guard ---------------------------------------------------------
# Default install dir is under %ProgramFiles% which requires Admin to write
# to. Skip the check when the operator overrode -InstallDir to a user-owned
# path (e.g. "C:\Users\me\squid-tentacle" for dev/test).
function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($InstallDir -eq $DefaultInstallDir -and -not (Test-IsAdministrator)) {
    Write-Error @"
Administrator privileges required for the default install dir ($DefaultInstallDir).

Either re-run from an elevated PowerShell:
    Start-Process pwsh -Verb RunAs -ArgumentList '-File', '$PSCommandPath'

Or install to a user-owned path:
    .\install-tentacle.ps1 -InstallDir "C:\Users\$env:USERNAME\squid-tentacle"
"@
    exit 1
}

Write-Host '=== Squid Tentacle Installer ==='
Write-Host "Version:  $Version"
Write-Host "Arch:     $rid"
Write-Host "Install:  $InstallDir"
Write-Host ''

# -- Download URL resolution -------------------------------------------------
# Mirrors install-tentacle.sh's two-path logic:
#   latest        → /releases/latest/download/squid-tentacle-{rid}.zip
#                   (GitHub redirects this to the highest non-prerelease tag)
#   <version>     → /releases/download/{version}/squid-tentacle-{version}-{rid}.zip
#                   with v-prefix fallback if the un-prefixed tag 404s
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

$urls = Resolve-DownloadUrls -Version $Version -Rid $rid -DownloadBase $DownloadBase

$tempDir = Join-Path $env:TEMP "squid-tentacle-install-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    $archivePath = Join-Path $tempDir 'tentacle.zip'

    $downloaded = $false
    foreach ($url in $urls) {
        Write-Host "Downloading from $url..."

        try {
            # -UseBasicParsing avoids needing IE first-run config on
            # stripped Server Core images. -TimeoutSec 300 = 5 min budget
            # mirrors install-tentacle.sh's `--max-time 300`.
            Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing -TimeoutSec 300
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

    # -- Extract --------------------------------------------------------------
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    Write-Host "Extracting to $InstallDir..."
    # -Force overwrites an existing install - same idempotent re-run UX as
    # install-tentacle.sh (tar xzf overwrites). The Tentacle service should
    # be stopped first if upgrading; the in-UI upgrade flow handles that
    # via the upgrade PowerShell script. For a manual re-install, the
    # operator should `sc stop squid-tentacle` first.
    Expand-Archive -Path $archivePath -DestinationPath $InstallDir -Force

    $binaryPath = Join-Path $InstallDir $BinaryName
    if (-not (Test-Path $binaryPath)) {
        Write-Error "Extraction completed but $BinaryName is missing from $InstallDir. The archive may be corrupt."
        exit 1
    }

    Write-Host "Installed: $binaryPath"
} finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# -- Firewall rule for Listening Tentacle ------------------------------------
# Idempotent: New-NetFirewallRule throws if the rule already exists, so
# Get-NetFirewallRule first. Same defensive pattern Octopus's automation
# docs use (`netsh advfirewall firewall add rule`). Phase-12.E.0 ships
# the rule unconditionally because the operator can't easily know yet
# whether they'll register as Listening or Polling - adding it always is
# cheap (one inbound TCP allow) and saves a separate operator step.
function Add-ListeningFirewallRule {
    param(
        [string] $RuleName,
        [int]    $Port
    )

    $existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue

    if ($existing) {
        Write-Host "Firewall rule '$RuleName' already exists - skipping."
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

# -- Service install (Phase 12.C path) ---------------------------------------
# `squid-tentacle service install` routes through WindowsServiceHost (Phase
# 12.C) → BuildScCreateArgs → sc create + sc failure (Phase 12.D) + sc start.
# The service installs as LocalSystem by default (Phase 12.C contract). LSA
# SeServiceLogonRight grant for non-LocalSystem identities is Phase 12.C-
# followup scope.
if ($NoServiceInstall) {
    Write-Host ''
    Write-Host '-NoServiceInstall set - skipping service install.'
    Write-Host "To install the service later, run:"
    Write-Host "  & '$binaryPath' service install --instance Default --service-name $ServiceName"
    Write-Host ''
} else {
    Write-Host ''
    Write-Host "Installing Windows service '$ServiceName'..."

    # ServiceCommand.Install internally derives the SCM Description as
    # "Squid Tentacle Agent ({instance})" - no --display-name flag exposed.
    # The instance name argument is positional after `--instance`, defaulting
    # to "Default" when omitted (which yields service name "squid-tentacle"
    # via ServiceCommand's default-instance branch). Pass it explicitly so the
    # operator-facing service name matches -ServiceName.
    & $binaryPath service install --instance Default --service-name $ServiceName

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Service install failed (exit code $LASTEXITCODE). Check the output above for details."
        exit $LASTEXITCODE
    }

    Write-Host "Service '$ServiceName' installed and started."
    Write-Host ''
}

# -- Next-step hints ---------------------------------------------------------
# Squid CLI shape:
#   register        - registers the agent with the Squid server, persists config
#   --server URL    - Squid server URL
#   --api-key KEY   - issued by the Squid server admin under User → API Keys
#   --role ROLE     - target tag (single value; pass --role multiple times for several tags)
#   --environment   - environment name (same multi-value pattern as --role)
#   --comms-url URL - set for Polling agents (where the agent dials Squid back)
# The Listening vs Polling distinction is implicit in whether --comms-url
# is set. There's no Octopus-style `--comms-style TentaclePassive/Active`.
Write-Host '=== Next steps ==='
Write-Host ''
Write-Host 'Register the agent with your Squid server:'
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
