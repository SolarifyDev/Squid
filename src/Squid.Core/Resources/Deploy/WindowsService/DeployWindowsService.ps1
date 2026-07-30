Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SquidParameter {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = ''
    )

    if ($SquidParameters.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace($SquidParameters[$Name])) {
        return [string]$SquidParameters[$Name]
    }

    return $Default
}

function Test-True {
    param([string]$Value)
    return [string]::Equals($Value, 'true', [System.StringComparison]::OrdinalIgnoreCase)
}

function Invoke-Sc {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & sc.exe @Arguments | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$ScriptBlock,
        [Parameter(Mandatory = $true)][string]$Description,
        [int]$Attempts = 10,
        [int]$DelayMilliseconds = 500
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            & $ScriptBlock
            return
        }
        catch {
            if ($attempt -eq $Attempts) {
                throw
            }

            Write-Host "$Description failed on attempt $attempt/$Attempts; retrying in $DelayMilliseconds ms. $($_.Exception.Message)"
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Wait-ServiceStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][System.ServiceProcess.ServiceControllerStatus]$Status,
        [int]$TimeoutSeconds = 60
    )

    $service = Get-Service -Name $Name -ErrorAction Stop
    $service.WaitForStatus($Status, [TimeSpan]::FromSeconds($TimeoutSeconds))
    $service.Refresh()

    if ($service.Status -ne $Status) {
        throw "Service '$Name' did not reach state '$Status' within $TimeoutSeconds seconds. Current state: '$($service.Status)'."
    }
}

function Wait-ServiceExists {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            return
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Service '$Name' was not visible to Get-Service within $TimeoutSeconds seconds after SCM create."
}

function Get-ServiceProcessId {
    param([Parameter(Mandatory = $true)][string]$Name)

    $escapedName = $Name.Replace("'", "''")
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedName'" -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return 0
    }

    return [int]$service.ProcessId
}

function Wait-ServiceProcessExit {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [int]$TimeoutSeconds = 30
    )

    if ($ProcessId -le 0) {
        return
    }

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            return
        }

        Wait-Process -Id $ProcessId -Timeout $TimeoutSeconds -ErrorAction Stop
    }
    catch {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            throw "Service process '$ProcessId' did not exit within $TimeoutSeconds seconds after service stop."
        }
    }
}

function Stop-ServiceIfRunning {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    $processId = Get-ServiceProcessId -Name $Name

    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Write-Host "Stopping Windows service '$Name' before reconfiguration."
        Stop-Service -Name $Name -Force -ErrorAction Stop
        Wait-ServiceStatus -Name $Name -Status ([System.ServiceProcess.ServiceControllerStatus]::Stopped)
        Wait-ServiceProcessExit -ProcessId $processId
    }
}

function Convert-StartModeForSc {
    param([string]$StartMode)

    switch ($StartMode.ToLowerInvariant()) {
        'automatic' { return 'auto' }
        'manual' { return 'demand' }
        'disabled' { return 'disabled' }
        default { throw "Unsupported Windows service start mode '$StartMode'. Expected Automatic, Manual, or Disabled." }
    }
}

function Split-Dependencies {
    param([string]$Dependencies)

    if ([string]::IsNullOrWhiteSpace($Dependencies)) {
        return @()
    }

    return $Dependencies -split "[,;`r`n]+" |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Resolve-AcquiredPackageSourcePath {
    $manifestPath = Join-Path (Get-Location) 'package-references.json'

    if (Test-Path -LiteralPath $manifestPath) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $entry = @($manifest) | Select-Object -First 1

            if ($null -ne $entry -and -not [string]::IsNullOrWhiteSpace([string]$entry.PackagePath)) {
                $candidate = [string]$entry.PackagePath

                if (-not [System.IO.Path]::IsPathRooted($candidate)) {
                    $candidate = Join-Path (Get-Location) $candidate
                }

                if (Test-Path -LiteralPath $candidate) {
                    return (Resolve-Path -LiteralPath $candidate).Path
                }
            }
        }
        catch {
            Write-Host "Unable to read package-references.json; falling back to package-references directory. $($_.Exception.Message)"
        }
    }

    $packageReferencesDir = Join-Path (Get-Location) 'package-references'
    if (Test-Path -LiteralPath $packageReferencesDir) {
        $candidate = Get-ChildItem -LiteralPath $packageReferencesDir | Sort-Object Name | Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    return ''
}

function Resolve-PackageRoot {
    $sourcePath = Get-SquidParameter 'Squid.Action.WindowsService.Package.SourcePath'
    $extractTo = Get-SquidParameter 'Squid.Action.WindowsService.Package.ExtractTo'
    $purgeBeforeExtract = Test-True (Get-SquidParameter 'Squid.Action.WindowsService.Package.PurgeBeforeExtract' 'False')

    if ([string]::IsNullOrWhiteSpace($sourcePath)) {
        $sourcePath = Resolve-AcquiredPackageSourcePath
    }

    if (-not [string]::IsNullOrWhiteSpace($sourcePath) -and (Test-Path -LiteralPath $sourcePath)) {
        $sourceItem = Get-Item -LiteralPath $sourcePath

        if ($sourceItem.PSIsContainer) {
            if ([string]::IsNullOrWhiteSpace($extractTo)) {
                return $sourceItem.FullName
            }

            if ($purgeBeforeExtract -and (Test-Path -LiteralPath $extractTo)) {
                Invoke-WithRetry -Description "Removing existing package extract directory '$extractTo'" -ScriptBlock {
                    Remove-Item -LiteralPath $extractTo -Recurse -Force
                }
            }

            New-Item -ItemType Directory -Path $extractTo -Force | Out-Null
            Invoke-WithRetry -Description "Copying package content from '$($sourceItem.FullName)' to '$extractTo'" -ScriptBlock {
                Copy-Item -Path (Join-Path $sourceItem.FullName '*') -Destination $extractTo -Recurse -Force
            }
            return (Resolve-Path -LiteralPath $extractTo).Path
        }

        throw "Windows service package source '$($sourceItem.FullName)' is a file. Deploy Windows Service expects package acquisition to provide an extracted package directory; do not pass a raw .nupkg/.zip archive to the action script."
    }

    if (-not [string]::IsNullOrWhiteSpace($extractTo) -and (Test-Path -LiteralPath $extractTo)) {
        return (Resolve-Path -LiteralPath $extractTo).Path
    }

    $workingDirectory = (Get-Location).Path
    throw "No Windows service package source was available for this action. Squid expected either Squid.Action.WindowsService.Package.SourcePath, a package-references.json entry, a package-references directory, or an existing Squid.Action.WindowsService.Package.ExtractTo path. Working directory: '$workingDirectory'. Verify the release selected package is bound to this action name and that the Acquire Packages step completed."
}

function Resolve-ExecutablePath {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$ExecutablePath
    )

    if ([System.IO.Path]::IsPathRooted($ExecutablePath)) {
        return $ExecutablePath
    }

    return Join-Path $PackageRoot $ExecutablePath
}

function Build-BinaryPathName {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [string]$Arguments
    )

    $binary = '"' + $ExecutablePath + '"'
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $binary += ' ' + $Arguments
    }

    return $binary
}

function Get-ServiceAccountArgs {
    $serviceAccount = Get-SquidParameter 'Squid.Action.WindowsService.ServiceAccount' 'LocalSystem'
    $customAccountName = Get-SquidParameter 'Squid.Action.WindowsService.CustomAccountName'
    $customAccountPassword = Get-SquidParameter 'Squid.Action.WindowsService.CustomAccountPassword'

    switch ($serviceAccount.ToLowerInvariant()) {
        'localsystem' { return @('obj=', 'LocalSystem') }
        'networkservice' { return @('obj=', 'NT AUTHORITY\NetworkService') }
        'localservice' { return @('obj=', 'NT AUTHORITY\LocalService') }
        'specificuser' {
            if ([string]::IsNullOrWhiteSpace($customAccountName)) {
                throw "Windows service account 'SpecificUser' requires Squid.Action.WindowsService.CustomAccountName."
            }

            if ([string]::IsNullOrWhiteSpace($customAccountPassword)) {
                throw "Windows service account 'SpecificUser' requires Squid.Action.WindowsService.CustomAccountPassword."
            }

            return @('obj=', $customAccountName, 'password=', $customAccountPassword)
        }
        default { throw "Unsupported Windows service account '$serviceAccount'. Expected LocalSystem, NetworkService, LocalService, or SpecificUser." }
    }
}

$createOrUpdate = Get-SquidParameter 'Squid.Action.WindowsService.CreateOrUpdateService' 'True'
if (-not (Test-True $createOrUpdate)) {
    Write-Host "Windows service create/update is disabled for this action."
    return
}

$serviceName = Get-SquidParameter 'Squid.Action.WindowsService.ServiceName'
if ([string]::IsNullOrWhiteSpace($serviceName)) {
    throw "Squid.Action.WindowsService.ServiceName is required."
}

$displayName = Get-SquidParameter 'Squid.Action.WindowsService.DisplayName' $serviceName
$description = Get-SquidParameter 'Squid.Action.WindowsService.Description'
$relativeExecutablePath = Get-SquidParameter 'Squid.Action.WindowsService.ExecutablePath'
if ([string]::IsNullOrWhiteSpace($relativeExecutablePath)) {
    throw "Squid.Action.WindowsService.ExecutablePath is required."
}

$arguments = Get-SquidParameter 'Squid.Action.WindowsService.Arguments'
$startMode = Get-SquidParameter 'Squid.Action.WindowsService.StartMode' 'Automatic'
$desiredStatus = Get-SquidParameter 'Squid.Action.WindowsService.DesiredStatus' 'Started'
$dependencies = @(Split-Dependencies (Get-SquidParameter 'Squid.Action.WindowsService.Dependencies'))
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($null -ne $existingService) {
    Stop-ServiceIfRunning -Name $serviceName
}

$packageRoot = Resolve-PackageRoot
$executablePath = Resolve-ExecutablePath -PackageRoot $packageRoot -ExecutablePath $relativeExecutablePath
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Windows service executable '$executablePath' was not found. Package root: '$packageRoot'."
}

$binaryPathName = Build-BinaryPathName -ExecutablePath $executablePath -Arguments $arguments
$scStartMode = Convert-StartModeForSc $startMode
$accountArgs = Get-ServiceAccountArgs

if ($null -eq $existingService) {
    Write-Host "Creating Windows service '$serviceName'."
    Invoke-Sc create $serviceName binPath= $binaryPathName DisplayName= $displayName start= $scStartMode @accountArgs
    Wait-ServiceExists -Name $serviceName
} else {
    Write-Host "Reconfiguring Windows service '$serviceName'."
    Invoke-Sc config $serviceName binPath= $binaryPathName DisplayName= $displayName start= $scStartMode @accountArgs
}

if (-not [string]::IsNullOrWhiteSpace($description)) {
    Invoke-Sc description $serviceName $description
}

if ($dependencies.Count -gt 0) {
    Invoke-Sc config $serviceName depend= ($dependencies -join '/')
}

switch ($desiredStatus.ToLowerInvariant()) {
    'started' {
        Write-Host "Starting Windows service '$serviceName'."
        Start-Service -Name $serviceName -ErrorAction Stop
        Wait-ServiceStatus -Name $serviceName -Status ([System.ServiceProcess.ServiceControllerStatus]::Running)
    }
    'stopped' {
        Stop-ServiceIfRunning -Name $serviceName
    }
    default {
        throw "Unsupported Windows service desired status '$desiredStatus'. Expected Started or Stopped."
    }
}
