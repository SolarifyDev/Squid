#!/usr/bin/env bash
# ==============================================================================
# Phase 12.L.E.3 — minimal systemd-compatible test service.
#
# Counterpart to Squid.WindowsUpgradeE2E.TestService for Linux. Used by:
#   - LinuxServiceFixture: install + start + stop + uninstall against
#     a real systemd unit, no sudo-fail / SCM-timeout edge cases
#   - LinuxLifecycleContext (12.L.E.4): the upgrade .sh's Phase B targets
#     this service for `systemctl restart` swap mechanics
#
# Behaviour:
#   - On start: read $INSTALL_DIR/version.txt, write that version into
#     a marker file at $INSTALL_DIR/service-running.marker, then sleep
#     forever (until SIGTERM)
#   - On stop (SIGTERM): delete the marker, exit 0
#
# The marker file is the proxy for "service is running and has read its
# version" — same pattern as Windows TestUpgradeService. Tests assert
# marker presence + content as proof of a successful start cycle.
#
# Why a script (not a compiled binary):
#   - bash is universally available on every Linux distro the agent
#     supports — no .NET runtime needed for the test service itself
#   - Easier to reason about: one file, no cross-compile concerns
#   - Same observable contract as Windows test service (marker file)
#
# INSTALL_DIR is passed via env var by systemd's Environment= directive in
# the unit file the fixture writes. The script defaults to its own
# directory if INSTALL_DIR is unset (defensive — caller should set it).
# ==============================================================================

set -uo pipefail

INSTALL_DIR="${INSTALL_DIR:-$(dirname "$(readlink -f "$0")")}"
VERSION_FILE="$INSTALL_DIR/version.txt"
MARKER_FILE="$INSTALL_DIR/service-running.marker"

read_version() {
    if [ -f "$VERSION_FILE" ]; then
        cat "$VERSION_FILE" | tr -d '[:space:]'
    else
        echo "unknown"
    fi
}

cleanup() {
    rm -f "$MARKER_FILE" 2>/dev/null || true
    exit 0
}

trap cleanup TERM INT

# Write marker on start.
mkdir -p "$INSTALL_DIR" 2>/dev/null || true
read_version > "$MARKER_FILE"

# Sleep forever — systemd's main process. The trap above deletes the
# marker on stop, so absence of the marker is the proof of "service
# stopped cleanly" the fixture polls for.
while true; do
    sleep 60 &
    wait $!
done
