# ==========================================================================
# Squid Runtime Helper Functions (PowerShell)
# Injected by the SSH transport before every user PowerShell script.
# DO NOT EDIT - changes made inside the remote work directory are lost.
# ==========================================================================

function Convert-ToSquidBase64 {
    param([string]$Value)
    if ($null -eq $Value) { return '' }
    return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Value))
}

function Set-SquidVariable {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [string]$Value = '',
        [bool]$Sensitive = $false
    )
    $encodedName = Convert-ToSquidBase64 -Value $Name
    $encodedValue = Convert-ToSquidBase64 -Value $Value
    $sensitiveFlag = if ($Sensitive) { 'True' } else { 'False' }
    Write-Host ('##squid[setVariable name="{0}" value="{1}" sensitive=''{2}'']' -f $encodedName, $encodedValue, $sensitiveFlag)
}

function Get-SquidVariable {
    param([Parameter(Mandatory=$true)][string]$Name)
    $envName = ($Name -replace '[^A-Za-z0-9_]', '_')
    if ($envName -match '^[0-9]') { $envName = "_$envName" }
    return (Get-Item -Path "Env:$envName" -ErrorAction SilentlyContinue).Value
}

function New-SquidArtifact {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string]$Name
    )
    if ([string]::IsNullOrEmpty($Name)) { $Name = [System.IO.Path]::GetFileName($Path) }
    $encodedPath = Convert-ToSquidBase64 -Value $Path
    $encodedName = Convert-ToSquidBase64 -Value $Name
    Write-Host ('##squid[createArtifact path="{0}" name="{1}"]' -f $encodedPath, $encodedName)
}

function Invoke-SquidFailStep {
    param([string]$Message = 'Script requested step failure')
    $encoded = Convert-ToSquidBase64 -Value $Message
    Write-Host ('##squid[stepFailed message="{0}"]' -f $encoded)
    exit 1
}

function Write-SquidWarning {
    param([string]$Message = '')
    $encoded = Convert-ToSquidBase64 -Value $Message
    Write-Host ('##squid[stdWarning message="{0}"]' -f $encoded)
}
