#!/bin/bash
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

HELM_ARGS="upgrade --install $RELEASE_NAME $CHART_PATH --namespace $HELM_NAMESPACE"

if [ "$RESET_VALUES" = "True" ]; then
    HELM_ARGS="$HELM_ARGS --reset-values"
fi

if [ "$HELM_WAIT" = "True" ]; then
    HELM_ARGS="$HELM_ARGS --wait"
fi

# Values files
{{ValuesFilesBlock}}

# Key-value overrides
{{SetValuesBlock}}

if [ -n "$ADDITIONAL_ARGS" ]; then
    HELM_ARGS="$HELM_ARGS $ADDITIONAL_ARGS"
fi

echo "Running: $HELM_EXE $HELM_ARGS"
eval $HELM_EXE $HELM_ARGS

echo "Helm upgrade completed successfully"
