$ErrorActionPreference = "Stop"

# --- Kustomize deploy ---
$overlayPath = "{{OverlayPath}}"
$kustomizeExe = "{{KustomizeExe}}"
$additionalArgs = "{{AdditionalArgs}}"
$applyFlags = "{{ApplyFlags}}"

if ([string]::IsNullOrEmpty($kustomizeExe)) {
    $kustomizeExe = "kubectl kustomize"
}

if ([string]::IsNullOrEmpty($overlayPath)) {
    $overlayPath = "."
}

Write-Host "Running kustomize on: $overlayPath"

if ([string]::IsNullOrEmpty($additionalArgs)) {
    $kustomizeOutput = Invoke-Expression "$kustomizeExe `"$overlayPath`""
} else {
    $kustomizeOutput = Invoke-Expression "$kustomizeExe `"$overlayPath`" $additionalArgs"
}

if ($LASTEXITCODE -ne 0) { throw "Kustomize build failed" }

$kustomizeOutput | kubectl apply $applyFlags -f -

if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed" }

Write-Host "Kustomize apply completed successfully"
