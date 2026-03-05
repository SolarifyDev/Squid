#!/usr/bin/env bash
set -euo pipefail

# --- Helm upgrade/install ---
RELEASE_NAME="{{ReleaseName}}"
CHART_PATH="{{ChartPath}}"
HELM_NAMESPACE="{{Namespace}}"
HELM_EXE="{{HelmExe}}"
RESET_VALUES="{{ResetValues}}"
HELM_WAIT="{{HelmWait}}"
ADDITIONAL_ARGS="{{AdditionalArgs}}"

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
