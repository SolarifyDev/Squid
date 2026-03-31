#!/usr/bin/env bash
set -euo pipefail
umask 077
b64d() { echo -n "$1" | base64 --decode; }

# --- Configure kubectl context ---
KUBECONFIG_PATH="$(mktemp /tmp/kubectl-config-XXXXXX)"
export KUBECONFIG="$KUBECONFIG_PATH"

KUBECTL_EXE="$(b64d '{{KubectlExe}}')"
if [ -z "$KUBECTL_EXE" ]; then
    KUBECTL_EXE="kubectl"
fi

CERT_PATH=""
CLIENT_CERT_PATH=""
CLIENT_KEY_PATH=""
GKE_KEY_FILE=""
AWS_WEB_IDENTITY_FILE=""
CRED_FILE=""
AZURE_CONFIG_DIR=""

# No trap — kubeconfig must persist for the user script process.
# Temp credential files are cleaned up after kubectl config calls below.
# Kubeconfig file is cleaned up when the work directory is deleted.

CLUSTER_URL="$(b64d '{{ClusterUrl}}')"
ACCOUNT_TYPE="$(b64d '{{AccountType}}')"
SKIP_TLS="$(b64d '{{SkipTlsVerification}}')"
NAMESPACE="$(b64d '{{Namespace}}')"
CLUSTER_NAME="squid-cluster"
CONTEXT_NAME="squid-context"
USER_NAME="squid-user"

# Set cluster
CLUSTER_CMD=("$KUBECTL_EXE" config set-cluster "$CLUSTER_NAME" "--server=$CLUSTER_URL")

if [ "$SKIP_TLS" = "True" ]; then
    CLUSTER_CMD+=("--insecure-skip-tls-verify=true")
fi

CLUSTER_CERTIFICATE="$(b64d '{{ClusterCertificate}}')"
if [ -n "$CLUSTER_CERTIFICATE" ]; then
    CERT_PATH="$(mktemp /tmp/ca-cert-XXXXXX)"
    echo "$CLUSTER_CERTIFICATE" > "$CERT_PATH"
    CLUSTER_CMD+=("--certificate-authority=$CERT_PATH")
fi

"${CLUSTER_CMD[@]}" || { echo "ERROR: kubectl config set-cluster failed" >&2; exit 1; }

# Set credentials based on account type
case "$ACCOUNT_TYPE" in
    "Token")
        CRED_FILE="$(mktemp /tmp/cred-token-XXXXXX)"
        b64d '{{Token}}' > "$CRED_FILE"
        TOKEN="$(cat "$CRED_FILE")"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" --token="$TOKEN" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "UsernamePassword")
        CRED_FILE="$(mktemp /tmp/cred-pass-XXXXXX)"
        b64d '{{Password}}' > "$CRED_FILE"
        AUTH_USERNAME="$(b64d '{{Username}}')"
        AUTH_PASSWORD="$(cat "$CRED_FILE")"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" --username="$AUTH_USERNAME" --password="$AUTH_PASSWORD" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "ClientCertificate")
        CLIENT_CERT="$(b64d '{{ClientCertificateData}}')"
        CLIENT_KEY="$(b64d '{{ClientCertificateKeyData}}')"
        CLIENT_CERT_PATH="$(mktemp /tmp/client-cert-XXXXXX)"
        CLIENT_KEY_PATH="$(mktemp /tmp/client-key-XXXXXX)"
        echo "$CLIENT_CERT" > "$CLIENT_CERT_PATH"
        echo "$CLIENT_KEY" > "$CLIENT_KEY_PATH"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" --client-certificate="$CLIENT_CERT_PATH" --client-key="$CLIENT_KEY_PATH" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "AmazonWebServicesAccount")
        AWS_CLUSTER_NAME="$(b64d '{{AwsClusterName}}')"
        AWS_REGION="$(b64d '{{AwsRegion}}')"
        if [ -z "$AWS_CLUSTER_NAME" ] || [ -z "$AWS_REGION" ]; then
            echo "ERROR: AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)" >&2
            exit 1
        fi
        CRED_FILE="$(mktemp /tmp/cred-aws-XXXXXX)"
        b64d '{{SecretKey}}' > "$CRED_FILE"
        export AWS_ACCESS_KEY_ID="$(b64d '{{AccessKey}}')"
        export AWS_SECRET_ACCESS_KEY="$(cat "$CRED_FILE")"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" \
            --exec-api-version=client.authentication.k8s.io/v1beta1 \
            --exec-command=aws \
            --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg="$AWS_CLUSTER_NAME" --exec-arg="--region" --exec-arg="$AWS_REGION" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "AmazonWebServicesRoleAccount")
        AWS_CLUSTER_NAME="$(b64d '{{AwsClusterName}}')"
        AWS_REGION="$(b64d '{{AwsRegion}}')"
        if [ -z "$AWS_CLUSTER_NAME" ] || [ -z "$AWS_REGION" ]; then
            echo "ERROR: AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)" >&2
            exit 1
        fi
        AWS_ROLE_ARN="$(b64d '{{AwsAssumeRoleArn}}')"
        AWS_SESSION_DURATION="$(b64d '{{AwsAssumeRoleSessionDuration}}')"
        AWS_EXTERNAL_ID="$(b64d '{{AwsAssumeRoleExternalId}}')"
        CRED_FILE="$(mktemp /tmp/cred-aws-XXXXXX)"
        b64d '{{SecretKey}}' > "$CRED_FILE"
        export AWS_ACCESS_KEY_ID="$(b64d '{{AccessKey}}')"
        export AWS_SECRET_ACCESS_KEY="$(cat "$CRED_FILE")"
        ASSUME_CMD=(aws sts assume-role --role-arn "$AWS_ROLE_ARN" --role-session-name "squid-deploy")
        if [ -n "$AWS_SESSION_DURATION" ]; then ASSUME_CMD+=(--duration-seconds "$AWS_SESSION_DURATION"); fi
        if [ -n "$AWS_EXTERNAL_ID" ]; then ASSUME_CMD+=(--external-id "$AWS_EXTERNAL_ID"); fi
        ASSUMED="$("${ASSUME_CMD[@]}")" || { echo "ERROR: aws sts assume-role failed" >&2; exit 1; }
        export AWS_ACCESS_KEY_ID="$(echo "$ASSUMED" | python3 -c "import sys,json; print(json.load(sys.stdin)['Credentials']['AccessKeyId'])")"
        export AWS_SECRET_ACCESS_KEY="$(echo "$ASSUMED" | python3 -c "import sys,json; print(json.load(sys.stdin)['Credentials']['SecretAccessKey'])")"
        export AWS_SESSION_TOKEN="$(echo "$ASSUMED" | python3 -c "import sys,json; print(json.load(sys.stdin)['Credentials']['SessionToken'])")"
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" \
            --exec-api-version=client.authentication.k8s.io/v1beta1 \
            --exec-command=aws \
            --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg="$AWS_CLUSTER_NAME" --exec-arg="--region" --exec-arg="$AWS_REGION" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "AmazonWebServicesOidcAccount")
        AWS_CLUSTER_NAME="$(b64d '{{AwsClusterName}}')"
        AWS_REGION="$(b64d '{{AwsRegion}}')"
        if [ -z "$AWS_CLUSTER_NAME" ] || [ -z "$AWS_REGION" ]; then
            echo "ERROR: AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)" >&2
            exit 1
        fi
        AWS_ROLE_ARN="$(b64d '{{AwsRoleArn}}')"
        AWS_WEB_IDENTITY_FILE="$(mktemp /tmp/aws-token-XXXXXX)"
        b64d '{{AwsWebIdentityToken}}' > "$AWS_WEB_IDENTITY_FILE"
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
    "AwsEc2InstanceRole")
        AWS_CLUSTER_NAME="$(b64d '{{AwsClusterName}}')"
        AWS_REGION="$(b64d '{{AwsRegion}}')"
        if [ -z "$AWS_CLUSTER_NAME" ] || [ -z "$AWS_REGION" ]; then
            echo "ERROR: AWS EKS cluster name and region must be configured on the Kubernetes target (ProviderType=AwsEks with ClusterName and Region)" >&2
            exit 1
        fi
        "$KUBECTL_EXE" config set-credentials "$USER_NAME" \
            --exec-api-version=client.authentication.k8s.io/v1beta1 \
            --exec-command=aws \
            --exec-arg=eks --exec-arg=get-token --exec-arg="--cluster-name" --exec-arg="$AWS_CLUSTER_NAME" --exec-arg="--region" --exec-arg="$AWS_REGION" \
            || { echo "ERROR: kubectl config set-credentials failed" >&2; exit 1; }
        ;;
    "AzureServicePrincipal")
        AZURE_CONFIG_DIR="$(mktemp -d /tmp/azure-cli-XXXXXX)"
        export AZURE_CONFIG_DIR
        CRED_FILE="$(mktemp /tmp/cred-azure-XXXXXX)"
        b64d '{{AzureKey}}' > "$CRED_FILE"
        az login --service-principal \
            -u "$(b64d '{{AzureClientId}}')" -p "$(cat "$CRED_FILE")" --tenant "$(b64d '{{AzureTenantId}}')" \
            || { echo "ERROR: az login failed" >&2; exit 1; }
        az account set --subscription "$(b64d '{{AzureSubscriptionId}}')" \
            || { echo "ERROR: az account set failed" >&2; exit 1; }
        az aks get-credentials \
            --resource-group "$(b64d '{{AksClusterResourceGroup}}')" \
            --name "$(b64d '{{AksClusterName}}')" \
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
        AZURE_OIDC_TOKEN="$(b64d '{{AzureOidcToken}}')"
        az login --service-principal --federated-token "$AZURE_OIDC_TOKEN" \
            -u "$(b64d '{{AzureClientId}}')" --tenant "$(b64d '{{AzureTenantId}}')" \
            || { echo "ERROR: az login (OIDC) failed" >&2; exit 1; }
        az account set --subscription "$(b64d '{{AzureSubscriptionId}}')" \
            || { echo "ERROR: az account set failed" >&2; exit 1; }
        az aks get-credentials \
            --resource-group "$(b64d '{{AksClusterResourceGroup}}')" \
            --name "$(b64d '{{AksClusterName}}')" \
            --file "$KUBECONFIG_PATH" --overwrite-existing \
            || { echo "ERROR: az aks get-credentials failed" >&2; exit 1; }
        if command -v kubelogin &>/dev/null; then
            kubelogin convert-kubeconfig -l azurecli --kubeconfig "$KUBECONFIG_PATH" \
                || echo "Warning: kubelogin convert failed, continuing without it"
        fi
        ;;
    "GoogleCloudAccount")
        GKE_KEY_FILE="$(mktemp /tmp/gcp-key-XXXXXX)"
        b64d '{{GcpJsonKey}}' > "$GKE_KEY_FILE"
        gcloud auth activate-service-account --key-file="$GKE_KEY_FILE" \
            || { echo "ERROR: gcloud auth failed" >&2; exit 1; }
        GKE_ZONE="$(b64d '{{GkeZone}}')"
        GKE_REGION="$(b64d '{{GkeRegion}}')"
        GKE_LOC_FLAG=""
        if [ -n "$GKE_ZONE" ]; then GKE_LOC_FLAG="--zone=$GKE_ZONE"; fi
        if [ -n "$GKE_REGION" ]; then GKE_LOC_FLAG="--region=$GKE_REGION"; fi
        GKE_INTERNAL="$(b64d '{{GkeUseClusterInternalIp}}')"
        GKE_CMD=(gcloud container clusters get-credentials "$(b64d '{{GkeClusterName}}')" $GKE_LOC_FLAG --project="$(b64d '{{GkeProject}}')")
        if [ "$GKE_INTERNAL" = "True" ]; then GKE_CMD+=("--internal-ip"); fi
        export KUBECONFIG="$KUBECONFIG_PATH"
        "${GKE_CMD[@]}" || { echo "ERROR: gcloud get-credentials failed" >&2; exit 1; }
        ;;
esac

# --- Endpoint-level role assumption (applies after any AWS auth method) ---
AWS_EP_ROLE_ARN="$(b64d '{{AwsEndpointAssumeRoleArn}}')"
if [ -n "$AWS_EP_ROLE_ARN" ]; then
    AWS_EP_SESSION_DURATION="$(b64d '{{AwsEndpointAssumeRoleSessionDuration}}')"
    AWS_EP_EXTERNAL_ID="$(b64d '{{AwsEndpointAssumeRoleExternalId}}')"
    ASSUME_CMD=(aws sts assume-role --role-arn "$AWS_EP_ROLE_ARN" --role-session-name "squid-deploy")
    if [ -n "$AWS_EP_SESSION_DURATION" ]; then ASSUME_CMD+=(--duration-seconds "$AWS_EP_SESSION_DURATION"); fi
    if [ -n "$AWS_EP_EXTERNAL_ID" ]; then ASSUME_CMD+=(--external-id "$AWS_EP_EXTERNAL_ID"); fi
    echo "Assuming endpoint-level AWS role: $AWS_EP_ROLE_ARN"
    ASSUMED="$("${ASSUME_CMD[@]}")" || { echo "ERROR: aws sts assume-role failed for endpoint role" >&2; exit 1; }
    export AWS_ACCESS_KEY_ID="$(echo "$ASSUMED" | python3 -c "import sys,json; print(json.load(sys.stdin)['Credentials']['AccessKeyId'])")"
    export AWS_SECRET_ACCESS_KEY="$(echo "$ASSUMED" | python3 -c "import sys,json; print(json.load(sys.stdin)['Credentials']['SecretAccessKey'])")"
    export AWS_SESSION_TOKEN="$(echo "$ASSUMED" | python3 -c "import sys,json; print(json.load(sys.stdin)['Credentials']['SessionToken'])")"
fi

# Clean up temp credential files — kubeconfig references paths directly so certs must stay
rm -f "$CRED_FILE" 2>/dev/null || true
rm -f "$GKE_KEY_FILE" 2>/dev/null || true
rm -f "$AWS_WEB_IDENTITY_FILE" 2>/dev/null || true

# --- Proxy configuration ---
PROXY_HOST="$(b64d '{{ProxyHost}}')"
PROXY_PORT="$(b64d '{{ProxyPort}}')"
PROXY_USER="$(b64d '{{ProxyUsername}}')"
PROXY_PASS="$(b64d '{{ProxyPassword}}')"
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

# --- Output environment variables for the caller ---
echo "SQUID_KUBECONFIG=$KUBECONFIG_PATH"
if [ -n "${HTTPS_PROXY:-}" ]; then echo "SQUID_HTTPS_PROXY=$HTTPS_PROXY"; fi
if [ -n "${HTTP_PROXY:-}" ]; then echo "SQUID_HTTP_PROXY=$HTTP_PROXY"; fi
if [ -n "${NO_PROXY:-}" ]; then echo "SQUID_NO_PROXY=$NO_PROXY"; fi
if [ -n "${AZURE_CONFIG_DIR:-}" ]; then echo "SQUID_AZURE_CONFIG_DIR=$AZURE_CONFIG_DIR"; fi
