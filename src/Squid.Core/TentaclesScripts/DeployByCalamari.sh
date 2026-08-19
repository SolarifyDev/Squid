#!/usr/bin/env bash
set -e

export PATH="/squid/bin:$PATH"

squidCalamari=""
tentacleCommand="$(command -v squid-tentacle 2>/dev/null || true)"

if [ -n "$tentacleCommand" ]; then
    resolvedTentacle="$(readlink -f "$tentacleCommand" 2>/dev/null || printf '%s' "$tentacleCommand")"
    bundledCalamari="$(dirname "$resolvedTentacle")/squid-calamari"
    if [ -x "$bundledCalamari" ]; then
        squidCalamari="$bundledCalamari"
    fi
fi

if [ -z "$squidCalamari" ]; then
    squidCalamari="$(command -v squid-calamari 2>/dev/null || true)"
fi

if [ -z "$squidCalamari" ]; then
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

"$squidCalamari" "${ARGS[@]}"
