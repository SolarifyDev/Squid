$ErrorActionPreference = "Stop"
function B64D($s) { if ([string]::IsNullOrEmpty($s)) { return "" }; [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s)) }

# --- Configure kubectl context ---
$kubeconfigPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "kubectl-config-$([Guid]::NewGuid().ToString('N')).yaml")
$env:KUBECONFIG = $kubeconfigPath

$kubectlExe = B64D "{{KubectlExe}}"
if ([string]::IsNullOrEmpty($kubectlExe)) {
    $kubectlExe = "kubectl"
}

try {
    $certPath = $null
    $clientCertPath = $null
    $clientKeyPath = $null
    $gkeKeyFile = $null
    $awsWebIdentityFile = $null
    $credFile = $null

    $clusterUrl = B64D "{{ClusterUrl}}"
    $accountType = B64D "{{AccountType}}"
    $skipTls = B64D "{{SkipTlsVerification}}"
    $namespace = B64D "{{Namespace}}"
    $clusterName = "squid-cluster"
    $contextName = "squid-context"
    $userName = "squid-user"

    # Set cluster
    $clusterArgs = @("config", "set-cluster", $clusterName, "--server=$clusterUrl")

    if ($skipTls -eq "True") {
        $clusterArgs += "--insecure-skip-tls-verify=true"
    }

    $clusterCertificate = B64D "{{ClusterCertificate}}"
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
            $credFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "cred-token-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.File]::WriteAllText($credFile, (B64D "{{Token}}"))
            $token = [System.IO.File]::ReadAllText($credFile).Trim()
            & $kubectlExe config set-credentials $userName --token="$token"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "UsernamePassword" {
            $credFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "cred-pass-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.File]::WriteAllText($credFile, (B64D "{{Password}}"))
            $authUsername = B64D "{{Username}}"
            $authPassword = [System.IO.File]::ReadAllText($credFile).Trim()
            & $kubectlExe config set-credentials $userName --username="$authUsername" --password="$authPassword"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "ClientCertificate" {
            $clientCert = B64D "{{ClientCertificateData}}"
            $clientKey = B64D "{{ClientCertificateKeyData}}"
            $clientCertPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "client-cert-$([Guid]::NewGuid().ToString('N')).pem")
            $clientKeyPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "client-key-$([Guid]::NewGuid().ToString('N')).pem")
            [System.IO.File]::WriteAllText($clientCertPath, $clientCert)
            [System.IO.File]::WriteAllText($clientKeyPath, $clientKey)
            & $kubectlExe config set-credentials $userName --client-certificate="$clientCertPath" --client-key="$clientKeyPath"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "AmazonWebServicesAccount" {
            $awsClusterName = B64D "{{AwsClusterName}}"
            $awsRegion = B64D "{{AwsRegion}}"
            if ([string]::IsNullOrEmpty($awsClusterName) -or [string]::IsNullOrEmpty($awsRegion)) {
                throw "AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)"
            }
            $credFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "cred-aws-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.File]::WriteAllText($credFile, (B64D "{{SecretKey}}"))
            $env:AWS_ACCESS_KEY_ID = B64D "{{AccessKey}}"
            $env:AWS_SECRET_ACCESS_KEY = [System.IO.File]::ReadAllText($credFile).Trim()
            & $kubectlExe config set-credentials $userName `
                --exec-api-version=client.authentication.k8s.io/v1beta1 `
                --exec-command=aws `
                --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg="$awsClusterName" --exec-arg="--region" --exec-arg="$awsRegion"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "AmazonWebServicesRoleAccount" {
            $awsClusterName = B64D "{{AwsClusterName}}"
            $awsRegion = B64D "{{AwsRegion}}"
            if ([string]::IsNullOrEmpty($awsClusterName) -or [string]::IsNullOrEmpty($awsRegion)) {
                throw "AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)"
            }
            $awsRoleArn = B64D "{{AwsAssumeRoleArn}}"
            $awsSessionDuration = B64D "{{AwsAssumeRoleSessionDuration}}"
            $awsExternalId = B64D "{{AwsAssumeRoleExternalId}}"
            $credFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "cred-aws-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.File]::WriteAllText($credFile, (B64D "{{SecretKey}}"))
            $env:AWS_ACCESS_KEY_ID = B64D "{{AccessKey}}"
            $env:AWS_SECRET_ACCESS_KEY = [System.IO.File]::ReadAllText($credFile).Trim()
            $assumeArgs = @("sts", "assume-role", "--role-arn", $awsRoleArn, "--role-session-name", "squid-deploy")
            if ($awsSessionDuration -ne "") { $assumeArgs += @("--duration-seconds", $awsSessionDuration) }
            if ($awsExternalId -ne "") { $assumeArgs += @("--external-id", $awsExternalId) }
            $assumedJson = & aws @assumeArgs
            if ($LASTEXITCODE -ne 0) { throw "aws sts assume-role failed" }
            $assumed = $assumedJson | ConvertFrom-Json
            $env:AWS_ACCESS_KEY_ID = $assumed.Credentials.AccessKeyId
            $env:AWS_SECRET_ACCESS_KEY = $assumed.Credentials.SecretAccessKey
            $env:AWS_SESSION_TOKEN = $assumed.Credentials.SessionToken
            & $kubectlExe config set-credentials $userName `
                --exec-api-version=client.authentication.k8s.io/v1beta1 `
                --exec-command=aws `
                --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg="$awsClusterName" --exec-arg="--region" --exec-arg="$awsRegion"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "AmazonWebServicesOidcAccount" {
            $awsClusterName = B64D "{{AwsClusterName}}"
            $awsRegion = B64D "{{AwsRegion}}"
            if ([string]::IsNullOrEmpty($awsClusterName) -or [string]::IsNullOrEmpty($awsRegion)) {
                throw "AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)"
            }
            $awsRoleArn = B64D "{{AwsRoleArn}}"
            $awsWebIdentityFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "aws-token-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.File]::WriteAllText($awsWebIdentityFile, (B64D "{{AwsWebIdentityToken}}"))
            $env:AWS_WEB_IDENTITY_TOKEN_FILE = $awsWebIdentityFile
            $env:AWS_ROLE_ARN = $awsRoleArn
            & $kubectlExe config set-credentials $userName `
                --exec-api-version=client.authentication.k8s.io/v1beta1 `
                --exec-command=aws `
                --exec-arg=eks --exec-arg=get-token `
                --exec-arg="--cluster-name" --exec-arg="$awsClusterName" `
                --exec-arg="--region" --exec-arg="$awsRegion" `
                --exec-arg="--role-arn" --exec-arg="$awsRoleArn"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "AwsEc2InstanceRole" {
            $awsClusterName = B64D "{{AwsClusterName}}"
            $awsRegion = B64D "{{AwsRegion}}"
            if ([string]::IsNullOrEmpty($awsClusterName) -or [string]::IsNullOrEmpty($awsRegion)) {
                throw "AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)"
            }
            & $kubectlExe config set-credentials $userName `
                --exec-api-version=client.authentication.k8s.io/v1beta1 `
                --exec-command=aws `
                --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg="$awsClusterName" --exec-arg="--region" --exec-arg="$awsRegion"
            if ($LASTEXITCODE -ne 0) { throw "kubectl config set-credentials failed" }
        }
        "AzureServicePrincipal" {
            $azureConfigDir = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "azure-cli-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.Directory]::CreateDirectory($azureConfigDir) | Out-Null
            $env:AZURE_CONFIG_DIR = $azureConfigDir
            $credFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "cred-azure-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.File]::WriteAllText($credFile, (B64D "{{AzureKey}}"))
            $azureKey = [System.IO.File]::ReadAllText($credFile).Trim()
            & az login --service-principal -u (B64D "{{AzureClientId}}") -p "$azureKey" --tenant (B64D "{{AzureTenantId}}")
            if ($LASTEXITCODE -ne 0) { throw "az login failed" }
            & az account set --subscription (B64D "{{AzureSubscriptionId}}")
            if ($LASTEXITCODE -ne 0) { throw "az account set failed" }
            $aksAdminFlag = if ((B64D "{{AksUseAdminCredentials}}") -eq "True") { "--admin" } else { "" }
            if ($aksAdminFlag) {
                & az aks get-credentials --resource-group (B64D "{{AksClusterResourceGroup}}") --name (B64D "{{AksClusterName}}") --file $kubeconfigPath --overwrite-existing $aksAdminFlag
            } else {
                & az aks get-credentials --resource-group (B64D "{{AksClusterResourceGroup}}") --name (B64D "{{AksClusterName}}") --file $kubeconfigPath --overwrite-existing
            }
            if ($LASTEXITCODE -ne 0) { throw "az aks get-credentials failed" }
            $kubeloginPath = Get-Command kubelogin -ErrorAction SilentlyContinue
            if ($kubeloginPath) {
                & kubelogin convert-kubeconfig -l azurecli --kubeconfig $kubeconfigPath
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "kubelogin convert failed, continuing without it"
                }
            }
        }
        "AzureOidc" {
            $azureConfigDir = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "azure-cli-$([Guid]::NewGuid().ToString('N'))")
            [System.IO.Directory]::CreateDirectory($azureConfigDir) | Out-Null
            $env:AZURE_CONFIG_DIR = $azureConfigDir
            $azureOidcToken = B64D "{{AzureOidcToken}}"
            & az login --service-principal --federated-token $azureOidcToken -u (B64D "{{AzureClientId}}") --tenant (B64D "{{AzureTenantId}}")
            if ($LASTEXITCODE -ne 0) { throw "az login (OIDC) failed" }
            & az account set --subscription (B64D "{{AzureSubscriptionId}}")
            if ($LASTEXITCODE -ne 0) { throw "az account set failed" }
            $aksAdminFlag = if ((B64D "{{AksUseAdminCredentials}}") -eq "True") { "--admin" } else { "" }
            if ($aksAdminFlag) {
                & az aks get-credentials --resource-group (B64D "{{AksClusterResourceGroup}}") --name (B64D "{{AksClusterName}}") --file $kubeconfigPath --overwrite-existing $aksAdminFlag
            } else {
                & az aks get-credentials --resource-group (B64D "{{AksClusterResourceGroup}}") --name (B64D "{{AksClusterName}}") --file $kubeconfigPath --overwrite-existing
            }
            if ($LASTEXITCODE -ne 0) { throw "az aks get-credentials failed" }
            $kubeloginPath = Get-Command kubelogin -ErrorAction SilentlyContinue
            if ($kubeloginPath) {
                & kubelogin convert-kubeconfig -l azurecli --kubeconfig $kubeconfigPath
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "kubelogin convert failed, continuing without it"
                }
            }
        }
        "GoogleCloudAccount" {
            $gkeKeyFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "gcp-key-$([Guid]::NewGuid().ToString('N')).json")
            [System.IO.File]::WriteAllText($gkeKeyFile, (B64D "{{GcpJsonKey}}"))
            & gcloud auth activate-service-account --key-file="$gkeKeyFile"
            if ($LASTEXITCODE -ne 0) { throw "gcloud auth failed" }
            $gkeZone = B64D "{{GkeZone}}"
            $gkeRegion = B64D "{{GkeRegion}}"
            $gkeArgs = @("container", "clusters", "get-credentials", (B64D "{{GkeClusterName}}"), "--project=$(B64D '{{GkeProject}}')")
            if ($gkeZone -ne "") { $gkeArgs += "--zone=$gkeZone" }
            if ($gkeRegion -ne "") { $gkeArgs += "--region=$gkeRegion" }
            $gkeInternal = B64D "{{GkeUseClusterInternalIp}}"
            if ($gkeInternal -eq "True") { $gkeArgs += "--internal-ip" }
            $env:KUBECONFIG = $kubeconfigPath
            & gcloud @gkeArgs
            if ($LASTEXITCODE -ne 0) { throw "gcloud get-credentials failed" }
        }
    }

    # --- Endpoint-level role assumption (applies after any AWS auth method) ---
    $awsEpRoleArn = B64D "{{AwsEndpointAssumeRoleArn}}"
    if ($awsEpRoleArn -ne "") {
        $awsEpSessionDuration = B64D "{{AwsEndpointAssumeRoleSessionDuration}}"
        $awsEpExternalId = B64D "{{AwsEndpointAssumeRoleExternalId}}"
        $assumeArgs = @("sts", "assume-role", "--role-arn", $awsEpRoleArn, "--role-session-name", "squid-deploy")
        if ($awsEpSessionDuration -ne "") { $assumeArgs += @("--duration-seconds", $awsEpSessionDuration) }
        if ($awsEpExternalId -ne "") { $assumeArgs += @("--external-id", $awsEpExternalId) }
        Write-Host "Assuming endpoint-level AWS role: $awsEpRoleArn"
        $assumedJson = & aws @assumeArgs
        if ($LASTEXITCODE -ne 0) { throw "aws sts assume-role failed for endpoint role" }
        $assumed = $assumedJson | ConvertFrom-Json
        $env:AWS_ACCESS_KEY_ID = $assumed.Credentials.AccessKeyId
        $env:AWS_SECRET_ACCESS_KEY = $assumed.Credentials.SecretAccessKey
        $env:AWS_SESSION_TOKEN = $assumed.Credentials.SessionToken
    }

    # --- Proxy configuration ---
    $proxyHost = B64D "{{ProxyHost}}"
    $proxyPort = B64D "{{ProxyPort}}"
    $proxyUser = B64D "{{ProxyUsername}}"
    $proxyPass = B64D "{{ProxyPassword}}"
    if ($proxyHost -ne "") {
        if ($proxyUser -ne "") {
            $env:HTTPS_PROXY = "http://${proxyUser}:${proxyPass}@${proxyHost}:${proxyPort}"
        } else {
            $env:HTTPS_PROXY = "http://${proxyHost}:${proxyPort}"
        }
        $env:HTTP_PROXY = $env:HTTPS_PROXY
        $env:NO_PROXY = "localhost,127.0.0.1"
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
    foreach ($tempFile in @($kubeconfigPath, $certPath, $clientCertPath, $clientKeyPath, $gkeKeyFile, $awsWebIdentityFile, $credFile)) {
        if ($tempFile -and (Test-Path $tempFile -ErrorAction SilentlyContinue)) {
            Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
        }
    }
    if ($azureConfigDir -and (Test-Path $azureConfigDir -ErrorAction SilentlyContinue)) {
        Remove-Item $azureConfigDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
