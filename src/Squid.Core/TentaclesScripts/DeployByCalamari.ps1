# 检查 squid-calamari 是否存在（Kubernetes Agent / host runtime 需提供）
$squidCalamari = Get-Command "squid-calamari" -ErrorAction SilentlyContinue
if ($null -eq $squidCalamari) {
    Write-Error "squid-calamari not found in PATH"
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
& $squidCalamari.Source @commandArgs
