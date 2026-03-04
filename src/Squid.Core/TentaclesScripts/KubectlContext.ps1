$ErrorActionPreference = "Stop"

# --- Configure kubectl context ---
$kubeconfigPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "kubectl-config-$([Guid]::NewGuid().ToString('N')).yaml")
$env:KUBECONFIG = $kubeconfigPath

$kubectlExe = "{{KubectlExe}}"
if ([string]::IsNullOrEmpty($kubectlExe)) {
    $kubectlExe = "kubectl"
}

try {
    $certPath = $null
    $clientCertPath = $null
    $clientKeyPath = $null

    $clusterUrl = "{{ClusterUrl}}"
    $accountType = "{{AccountType}}"
    $skipTls = "{{SkipTlsVerification}}"
    $namespace = "{{Namespace}}"
    $clusterName = "squid-cluster"
    $contextName = "squid-context"
    $userName = "squid-user"

    # Set cluster
    $clusterArgs = @("config", "set-cluster", $clusterName, "--server=$clusterUrl")

    if ($skipTls -eq "True") {
        $clusterArgs += "--insecure-skip-tls-verify=true"
    }

    $clusterCertificate = "{{ClusterCertificate}}"
    if ($clusterCertificate -ne "") {
        $certPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "ca-cert-$([Guid]::NewGuid().ToString('N')).pem")
        [System.IO.File]::WriteAllText($certPath, $clusterCertificate)
        $clusterArgs += "--certificate-authority=$certPath"
    }

    & $kubectlExe @clusterArgs
    if ($LASTEXITCODE -ne 0) { throw "kubectl config set-cluster failed" }

    # Set credentials based on account type
    switch ($accountType) {
        "Token" {
            $token = "{{Token}}"
            & $kubectlExe config set-credentials $userName --token="$token"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "UsernamePassword" {
            $authUsername = "{{Username}}"
            $authPassword = "{{Password}}"
            & $kubectlExe config set-credentials $userName --username="$authUsername" --password="$authPassword"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "ClientCertificate" {
            $clientCert = "{{ClientCertificateData}}"
            $clientKey = "{{ClientCertificateKeyData}}"
            $clientCertPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "client-cert-$([Guid]::NewGuid().ToString('N')).pem")
            $clientKeyPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "client-key-$([Guid]::NewGuid().ToString('N')).pem")
            [System.IO.File]::WriteAllText($clientCertPath, $clientCert)
            [System.IO.File]::WriteAllText($clientKeyPath, $clientKey)
            & $kubectlExe config set-credentials $userName --client-certificate="$clientCertPath" --client-key="$clientKeyPath"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "AmazonWebServicesAccount" {
            $awsClusterName = "{{AwsClusterName}}"
            $awsRegion = "{{AwsRegion}}"
            $env:AWS_ACCESS_KEY_ID = "{{AccessKey}}"
            $env:AWS_SECRET_ACCESS_KEY = "{{SecretKey}}"
            & $kubectlExe config set-credentials $userName `
                --exec-api-version=client.authentication.k8s.io/v1beta1 `
                --exec-command=aws `
                --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg=$awsClusterName --exec-arg="--region" --exec-arg=$awsRegion
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
    }

    # Set context and use it
    & $kubectlExe config set-context $contextName --cluster=$clusterName --user=$userName --namespace=$namespace
    if ($LASTEXITCODE -ne 0) { throw "kubectl config set-context failed" }

    & $kubectlExe config use-context $contextName
    if ($LASTEXITCODE -ne 0) { throw "kubectl config use-context failed" }

    # Create namespace if it doesn't exist
    if ($namespace -ne "default" -and $namespace -ne "") {
        $existingNs = & $kubectlExe get namespace $namespace --ignore-not-found 2>&1
        if (-not $existingNs) {
            & $kubectlExe create namespace $namespace
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Failed to create namespace $namespace, it may already exist"
            }
        }
    }

    # --- Execute user script ---
    {{UserScript}}
}
finally {
    # Cleanup temp files (kubeconfig + certificates)
    foreach ($tempFile in @($kubeconfigPath, $certPath, $clientCertPath, $clientKeyPath)) {
        if ($tempFile -and (Test-Path $tempFile -ErrorAction SilentlyContinue)) {
            Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
        }
    }
}
