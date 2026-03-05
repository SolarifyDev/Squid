#!/usr/bin/env bash
set -e

export PATH="/squid/bin:$PATH"

if ! command -v squid-calamari &> /dev/null; then
    echo "squid-calamari not found in PATH" >&2
    exit 1
fi

if ! command -v kubectl &> /dev/null; then
    echo "kubectl not found in PATH" >&2
    exit 1
fi

ARGS=("apply-yaml" "--file={{PackageFilePath}}" "--variables={{VariableFilePath}}")

if [ -n "{{SensitiveVariableFile}}" ]; then
    ARGS+=("--sensitive={{SensitiveVariableFile}}" "--password={{SensitiveVariablePassword}}")
fi

squid-calamari "${ARGS[@]}"
