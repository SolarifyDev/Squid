#!/usr/bin/env bash
set -euo pipefail

export PATH="/squid/bin:$PATH"

if ! command -v squid-calamari >/dev/null 2>&1; then
    echo "squid-calamari not found in PATH" >&2
    exit 1
fi

ARGS=("deploy-package" "--archive={{PackageFilePath}}" "--variables={{VariableFilePath}}")

if [ -n "{{SensitiveVariableFile}}" ]; then
    ARGS+=("--sensitive={{SensitiveVariableFile}}" "--password={{SensitiveVariablePassword}}")
fi

squid-calamari "${ARGS[@]}"
