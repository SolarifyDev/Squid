#!/usr/bin/env bash
set -euo pipefail
b64d() { echo -n "$1" | base64 --decode; }

# --- Helm upgrade/install ---
RELEASE_NAME="$(b64d '{{ReleaseName}}')"
CHART_PATH="$(b64d '{{ChartPath}}')"
HELM_NAMESPACE="$(b64d '{{Namespace}}')"
HELM_EXE="$(b64d '{{HelmExe}}')"
RESET_VALUES="$(b64d '{{ResetValues}}')"
HELM_WAIT="$(b64d '{{HelmWait}}')"
ADDITIONAL_ARGS="$(b64d '{{AdditionalArgs}}')"

# Helm repo setup (populated when chart is sourced from a feed)
{{RepoSetupBlock}}

if [ -z "$HELM_EXE" ]; then
    HELM_EXE="helm"
fi

HELM_CMD=("$HELM_EXE" "upgrade" "--install" "$RELEASE_NAME" "$CHART_PATH" "--namespace" "$HELM_NAMESPACE")

if [ "$RESET_VALUES" = "True" ]; then
    HELM_CMD+=("--reset-values")
fi

if [ "$HELM_WAIT" = "True" ]; then
    HELM_CMD+=("--wait")
fi

HELM_WAIT_FOR_JOBS="$(b64d '{{WaitForJobs}}')"
if [ "$HELM_WAIT_FOR_JOBS" = "True" ]; then
    HELM_CMD+=("--wait-for-jobs")
fi

HELM_TIMEOUT="$(b64d '{{Timeout}}')"
if [ -n "$HELM_TIMEOUT" ]; then
    HELM_CMD+=("--timeout" "$HELM_TIMEOUT")
fi

CHART_VERSION="$(b64d '{{ChartVersion}}')"
if [ -n "$CHART_VERSION" ]; then
    HELM_CMD+=("--version" "$CHART_VERSION")
fi

# Values files
{{ValuesFilesBlock}}

# Key-value overrides
{{SetValuesBlock}}

if [ -n "$ADDITIONAL_ARGS" ]; then
    HELM_CMD+=($ADDITIONAL_ARGS)
fi

echo "Running: ${HELM_CMD[*]}"
"${HELM_CMD[@]}"

echo "Helm upgrade completed successfully"
