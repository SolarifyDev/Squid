#!/usr/bin/env bash
set -euo pipefail

# --- Configure kubectl context ---
KUBECONFIG_PATH="$(mktemp /tmp/kubectl-config-XXXXXX.yaml)"
export KUBECONFIG="$KUBECONFIG_PATH"

KUBECTL_EXE="{{KubectlExe}}"
if [ -z "$KUBECTL_EXE" ]; then
    KUBECTL_EXE="kubectl"
fi

CERT_PATH=""
CLIENT_CERT_PATH=""
CLIENT_KEY_PATH=""
GKE_KEY_FILE=""
AWS_WEB_IDENTITY_FILE=""

cleanup() {
    rm -f "$KUBECONFIG_PATH" 2>/dev/null || true
    rm -f "$CERT_PATH" 2>/dev/null || true
    rm -f "$CLIENT_CERT_PATH" 2>/dev/null || true
    rm -f "$CLIENT_KEY_PATH" 2>/dev/null || true
    rm -f "$GKE_KEY_FILE" 2>/dev/null || true
    rm -f "$AWS_WEB_IDENTITY_FILE" 2>/dev/null || true
    if [ -n "$AZURE_CONFIG_DIR" ] && [ -d "$AZURE_CONFIG_DIR" ]; then
        rm -rf "$AZURE_CONFIG_DIR" 2>/dev/null || true
    fi
}
trap cleanup EXIT

CLUSTER_URL="{{ClusterUrl}}"
ACCOUNT_TYPE="{{AccountType}}"
SKIP_TLS="{{SkipTlsVerification}}"
NAMESPACE="{{Namespace}}"
CLUSTER_NAME="squid-cluster"
CONTEXT_NAME="squid-context"
USER_NAME="squid-user"

# Set cluster
CLUSTER_CMD=("$KUBECTL_EXE" config set-cluster "$CLUSTER_NAME" "--server=$CLUSTER_URL")

if [ "$SKIP_TLS" = "True" ]; then
    CLUSTER_CMD+=("--insecure-skip-tls-verify=true")
fi

CLUSTER_CERTIFICATE="{{ClusterCertificate}}"
if [ -n "$CLUSTER_CERTIFICATE" ]; then
    CERT_PATH="$(mktemp /tmp/ca-cert-XXXXXX.pem)"
    echo "$CLUSTER_CERTIFICATE" > "$CERT_PATH"
    CLUSTER_CMD+=("--certificate-authority=$CERT_PATH")
fi

"${CLUSTER_CMD[@]}" || { echo "ERROR: kubectl config set-cluster failed" >&2; exit 1; }

# Set credentials based on account type
case "$ACCOUNT_TYPE" in
    "Token")
        TOKEN="{{Token}}"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" --token="$TOKEN" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "UsernamePassword")
        AUTH_USERNAME="{{Username}}"
        AUTH_PASSWORD="{{Password}}"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" --username="$AUTH_USERNAME" --password="$AUTH_PASSWORD" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "ClientCertificate")
        CLIENT_CERT="{{ClientCertificateData}}"
        CLIENT_KEY="{{ClientCertificateKeyData}}"
        CLIENT_CERT_PATH="$(mktemp /tmp/client-cert-XXXXXX.pem)"
        CLIENT_KEY_PATH="$(mktemp /tmp/client-key-XXXXXX.pem)"
        echo "$CLIENT_CERT" > "$CLIENT_CERT_PATH"
        echo "$CLIENT_KEY" > "$CLIENT_KEY_PATH"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" --client-certificate="$CLIENT_CERT_PATH" --client-key="$CLIENT_KEY_PATH" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "AmazonWebServicesAccount")
        AWS_CLUSTER_NAME="{{AwsClusterName}}"
        AWS_REGION="{{AwsRegion}}"
        export AWS_ACCESS_KEY_ID="{{AccessKey}}"
        export AWS_SECRET_ACCESS_KEY="{{SecretKey}}"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" \
            --exec-api-version=client.authentication.k8s.io/v1beta1 \
            --exec-command=aws \
            --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg="$AWS_CLUSTER_NAME" --exec-arg="--region" --exec-arg="$AWS_REGION" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "AmazonWebServicesOidcAccount")
        AWS_CLUSTER_NAME="{{AwsClusterName}}"
        AWS_REGION="{{AwsRegion}}"
        AWS_ROLE_ARN="{{AwsRoleArn}}"
        AWS_WEB_IDENTITY_FILE="$(mktemp /tmp/aws-token-XXXXXX)"
        echo "{{AwsWebIdentityToken}}" > "$AWS_WEB_IDENTITY_FILE"
        export AWS_WEB_IDENTITY_TOKEN_FILE="$AWS_WEB_IDENTITY_FILE"
        export AWS_ROLE_ARN="$AWS_ROLE_ARN"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" \
            --exec-api-version=client.authentication.k8s.io/v1beta1 \
            --exec-command=aws \
            --exec-arg=eks --exec-arg=get-token \
            --exec-arg="--cluster-name" --exec-arg="$AWS_CLUSTER_NAME" \
            --exec-arg="--region" --exec-arg="$AWS_REGION" \
            --exec-arg="--role-arn" --exec-arg="$AWS_ROLE_ARN" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "AzureServicePrincipal")
        AZURE_CONFIG_DIR="$(mktemp -d /tmp/azure-cli-XXXXXX)"
        export AZURE_CONFIG_DIR
        az login --service-principal \
            -u "{{AzureClientId}}" -p "{{AzureKey}}" --tenant "{{AzureTenantId}}" \
            || { echo "ERROR: az login failed" >&2; exit 1; }
        az account set --subscription "{{AzureSubscriptionId}}" \
            || { echo "ERROR: az account set failed" >&2; exit 1; }
        az aks get-credentials \
            --resource-group "{{AksClusterResourceGroup}}" \
            --name "{{AksClusterName}}" \
            --file "$KUBECONFIG_PATH" --overwrite-existing \
            || { echo "ERROR: az aks get-credentials failed" >&2; exit 1; }
        if command -v kubelogin &>/dev/null; then
            kubelogin convert-kubeconfig -l azurecli --kubeconfig "$KUBECONFIG_PATH" \
                || echo "Warning: kubelogin convert failed, continuing without it"
        fi
        ;;
    "AzureOidc")
        AZURE_CONFIG_DIR="$(mktemp -d /tmp/azure-cli-XXXXXX)"
        export AZURE_CONFIG_DIR
        AZURE_OIDC_TOKEN="{{AzureOidcToken}}"
        az login --service-principal --federated-token "$AZURE_OIDC_TOKEN" \
            -u "{{AzureClientId}}" --tenant "{{AzureTenantId}}" \
            || { echo "ERROR: az login (OIDC) failed" >&2; exit 1; }
        az account set --subscription "{{AzureSubscriptionId}}" \
            || { echo "ERROR: az account set failed" >&2; exit 1; }
        az aks get-credentials \
            --resource-group "{{AksClusterResourceGroup}}" \
            --name "{{AksClusterName}}" \
            --file "$KUBECONFIG_PATH" --overwrite-existing \
            || { echo "ERROR: az aks get-credentials failed" >&2; exit 1; }
        if command -v kubelogin &>/dev/null; then
            kubelogin convert-kubeconfig -l azurecli --kubeconfig "$KUBECONFIG_PATH" \
                || echo "Warning: kubelogin convert failed, continuing without it"
        fi
        ;;
    "GoogleCloudAccount")
        GKE_KEY_FILE="$(mktemp /tmp/gcp-key-XXXXXX.json)"
        echo "{{GcpJsonKey}}" > "$GKE_KEY_FILE"
        gcloud auth activate-service-account --key-file="$GKE_KEY_FILE" \
            || { echo "ERROR: gcloud auth failed" >&2; exit 1; }
        GKE_ZONE="{{GkeZone}}"
        GKE_REGION="{{GkeRegion}}"
        GKE_LOC_FLAG=""
        if [ -n "$GKE_ZONE" ]; then GKE_LOC_FLAG="--zone=$GKE_ZONE"; fi
        if [ -n "$GKE_REGION" ]; then GKE_LOC_FLAG="--region=$GKE_REGION"; fi
        GKE_INTERNAL="{{GkeUseClusterInternalIp}}"
        GKE_CMD=(gcloud container clusters get-credentials "{{GkeClusterName}}" $GKE_LOC_FLAG --project="{{GkeProject}}")
        if [ "$GKE_INTERNAL" = "True" ]; then GKE_CMD+=("--internal-ip"); fi
        export KUBECONFIG="$KUBECONFIG_PATH"
        "${GKE_CMD[@]}" || { echo "ERROR: gcloud get-credentials failed" >&2; exit 1; }
        ;;
esac

# --- Proxy configuration ---
PROXY_HOST="{{ProxyHost}}"
PROXY_PORT="{{ProxyPort}}"
PROXY_USER="{{ProxyUsername}}"
PROXY_PASS="{{ProxyPassword}}"
if [ -n "$PROXY_HOST" ]; then
    if [ -n "$PROXY_USER" ]; then
        export HTTPS_PROXY="http://${PROXY_USER}:${PROXY_PASS}@${PROXY_HOST}:${PROXY_PORT}"
    else
        export HTTPS_PROXY="http://${PROXY_HOST}:${PROXY_PORT}"
    fi
    export HTTP_PROXY="$HTTPS_PROXY"
    export NO_PROXY="localhost,127.0.0.1"
fi

# Set context and use it
"$KUBECTL_EXE" config set-context "$CONTEXT_NAME" --cluster="$CLUSTER_NAME" --user="$USER_NAME" --namespace="$NAMESPACE" \
    || { echo "ERROR: kubectl config set-context failed" >&2; exit 1; }
"$KUBECTL_EXE" config use-context "$CONTEXT_NAME" \
    || { echo "ERROR: kubectl config use-context failed" >&2; exit 1; }

# Create namespace if it doesn't exist
if [ "$NAMESPACE" != "default" ] && [ -n "$NAMESPACE" ]; then
    "$KUBECTL_EXE" get namespace -o name 2>/dev/null | grep -qx "namespace/$NAMESPACE" || \
        "$KUBECTL_EXE" create namespace "$NAMESPACE" || echo "Warning: Failed to create namespace $NAMESPACE, it may already exist"
fi

# --- Execute user script ---
{{UserScript}}
