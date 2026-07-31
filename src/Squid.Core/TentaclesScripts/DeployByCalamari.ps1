# Windows Tentacle installation records the active binary under ProgramData.
# Prefer its sibling bundled Calamari so deployment does not depend on machine PATH.
$squidCalamari = $null
if ($env:OS -eq 'Windows_NT' -and -not [string]::IsNullOrWhiteSpace($env:ProgramData)) {
    $installInfoPath = Join-Path $env:ProgramData 'Squid\Tentacle\install-info.json'

    if (Test-Path -LiteralPath $installInfoPath) {
        try {
            $installInfo = Get-Content -LiteralPath $installInfoPath -Raw | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace($installInfo.BinaryPath)) {
                $candidate = Join-Path (Split-Path -Parent $installInfo.BinaryPath) 'squid-calamari.exe'
                if (Test-Path -LiteralPath $candidate) {
                    $squidCalamari = $candidate
                }
            }
        } catch {
            Write-Verbose "Could not read Tentacle install-info.json: $($_.Exception.Message)"
        }
    }
}

if (-not $squidCalamari) {
    $squidCalamariCommand = if ($env:OS -eq 'Windows_NT') {
        Get-Command -Name 'squid-calamari.exe' -CommandType Application -ErrorAction SilentlyContinue
    } else {
        Get-Command -Name 'squid-calamari' -CommandType Application -ErrorAction SilentlyContinue
    }

    if ($squidCalamariCommand) {
        $squidCalamari = $squidCalamariCommand.Path
    }
}

if (-not $squidCalamari) {
    Write-Error "Bundled squid-calamari was not found beside the installed Tentacle, and no squid-calamari command was found in PATH."
    Exit 1
}

if ($null -eq (Get-Command "kubectl" -ErrorAction SilentlyContinue)) {
    Write-Error "kubectl not found in PATH"
    Exit 1
}

if ($null -eq (Get-Command "bash" -ErrorAction SilentlyContinue)) {
    Write-Error "bash not found in PATH (required by squid-calamari script execution)"
    Exit 1
}

$commandArgs = @(
    "apply-yaml",
    "--file={{PackageFilePath}}",
    "--variables={{VariableFilePath}}"
)

if ("{{SensitiveVariableFile}}" -ne "") {
    $commandArgs += "--sensitive={{SensitiveVariableFile}}"
    $commandArgs += "--password={{SensitiveVariablePassword}}"
}

# 调用 squid-calamari 原生命令（--file 支持 yaml/zip/nupkg）
& $squidCalamari @commandArgs
