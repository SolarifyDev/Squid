#!/usr/bin/env bash
set -euo pipefail

# --- Kustomize deploy ---
OVERLAY_PATH="{{OverlayPath}}"
KUSTOMIZE_EXE="{{KustomizeExe}}"
ADDITIONAL_ARGS="{{AdditionalArgs}}"
APPLY_FLAGS="{{ApplyFlags}}"

if [ -z "$KUSTOMIZE_EXE" ]; then
    KUSTOMIZE_EXE="kubectl kustomize"
fi

if [ -z "$OVERLAY_PATH" ]; then
    OVERLAY_PATH="."
fi

echo "Running kustomize on: $OVERLAY_PATH"

if [ -n "$ADDITIONAL_ARGS" ]; then
    $KUSTOMIZE_EXE "$OVERLAY_PATH" $ADDITIONAL_ARGS | kubectl apply $APPLY_FLAGS -f -
else
    $KUSTOMIZE_EXE "$OVERLAY_PATH" | kubectl apply $APPLY_FLAGS -f -
fi

echo "Kustomize apply completed successfully"
