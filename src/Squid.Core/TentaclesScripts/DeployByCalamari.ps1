# 从Server端传递版本信息
$calamariVersion = "{{CalamariVersion}}"
$calamariPath = "${env:TentacleHome}\Calamari\$calamariVersion"
$calamariExe = "$calamariPath\Calamari.exe"

# 检查Calamari是否存在
if (-not (Test-Path $calamariExe)) {
    Write-Error "Calamari not found at: $calamariExe"
    Exit 1
}

$sensitiveVariables = ""
if ("{{SensitiveVariableFile}}" -ne "") {
    $sensitiveVariables = "--sensitiveVariables={{SensitiveVariableFile}} --sensitiveVariablesPassword={{SensitiveVariablePassword}}"
}

# 调用Calamari
& $calamariExe kubernetes-apply-raw-yaml --package={{PackageFilePath}} --variables={{VariableFilePath}} $sensitiveVariables