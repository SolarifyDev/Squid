$squidCalamari = Get-Command "squid-calamari" -ErrorAction SilentlyContinue
if ($null -eq $squidCalamari) {
    Write-Error "squid-calamari not found in PATH"
    Exit 1
}

$commandArgs = @(
    "deploy-package",
    "--archive={{PackageFilePath}}",
    "--variables={{VariableFilePath}}"
)

if ("{{SensitiveVariableFile}}" -ne "") {
    $commandArgs += "--sensitive={{SensitiveVariableFile}}"
    $commandArgs += "--password={{SensitiveVariablePassword}}"
}

& $squidCalamari.Source @commandArgs
if ($LASTEXITCODE -ne 0) {
    Exit $LASTEXITCODE
}
