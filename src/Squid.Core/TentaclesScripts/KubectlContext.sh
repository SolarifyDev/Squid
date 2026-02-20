#!/bin/bash
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

cleanup() {
    rm -f "$KUBECONFIG_PATH" 2>/dev/null || true
    rm -f "$CERT_PATH" 2>/dev/null || true
    rm -f "$CLIENT_CERT_PATH" 2>/dev/null || true
    rm -f "$CLIENT_KEY_PATH" 2>/dev/null || true
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
esac

# Set context and use it
"$KUBECTL_EXE" config set-context "$CONTEXT_NAME" --cluster="$CLUSTER_NAME" --user="$USER_NAME" --namespace="$NAMESPACE" \
    || { echo "ERROR: kubectl config set-context failed" >&2; exit 1; }
"$KUBECTL_EXE" config use-context "$CONTEXT_NAME" \
    || { echo "ERROR: kubectl config use-context failed" >&2; exit 1; }

# Create namespace if it doesn't exist
if [ "$NAMESPACE" != "default" ] && [ -n "$NAMESPACE" ]; then
    "$KUBECTL_EXE" get namespace "$NAMESPACE" --ignore-not-found 2>/dev/null | grep -q "$NAMESPACE" || \
        "$KUBECTL_EXE" create namespace "$NAMESPACE" || echo "Warning: Failed to create namespace $NAMESPACE, it may already exist"
fi

# --- Execute user script ---
{{UserScript}}
