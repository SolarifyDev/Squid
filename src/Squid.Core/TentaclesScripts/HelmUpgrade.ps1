$ErrorActionPreference = "Stop"

# --- Helm upgrade/install ---
$releaseName = "{{ReleaseName}}"
$chartPath = "{{ChartPath}}"
$helmNamespace = "{{Namespace}}"
$helmExe = "{{HelmExe}}"
$resetValues = "{{ResetValues}}"
$helmWait = "{{HelmWait}}"
$additionalArgs = "{{AdditionalArgs}}"

# Helm repo setup (populated when chart is sourced from a feed)
{{RepoSetupBlock}}

if ([string]::IsNullOrEmpty($helmExe)) {
    $helmExe = "helm"
}

$helmArgs = @("upgrade", "--install", $releaseName, $chartPath, "--namespace", $helmNamespace)

if ($resetValues -eq "True") {
    $helmArgs += "--reset-values"
}

if ($helmWait -eq "True") {
    $helmArgs += "--wait"
}

$helmWaitForJobs = "{{WaitForJobs}}"
if ($helmWaitForJobs -eq "True") {
    $helmArgs += "--wait-for-jobs"
}

$helmTimeout = "{{Timeout}}"
if ($helmTimeout -ne "") {
    $helmArgs += "--timeout"
    $helmArgs += $helmTimeout
}

$chartVersion = "{{ChartVersion}}"
if ($chartVersion -ne "") {
    $helmArgs += "--version"
    $helmArgs += $chartVersion
}

# Values files
{{ValuesFilesBlock}}

# Key-value overrides
{{SetValuesBlock}}

if (-not [string]::IsNullOrEmpty($additionalArgs)) {
    $helmArgs += ($additionalArgs -split '\s+(?=--?)')  | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
}

Write-Host "Running: $helmExe $($helmArgs -join ' ')"
& $helmExe @helmArgs

if ($LASTEXITCODE -ne 0) {
    throw "Helm upgrade failed with exit code $LASTEXITCODE"
}

Write-Host "Helm upgrade completed successfully"
