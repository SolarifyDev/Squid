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

# Optional healthz endpoint for J.L.E.6+ full-lifecycle tests. The .sh's
# Phase B healthcheck loop curls $HEALTHCHECK_URL (default
# http://127.0.0.1:8080/healthz) — without an actual responder the
# upgrade always rolls back. python3 ships on every modern Linux distro
# (and on the GHA ubuntu-latest runner) — use it to spawn a tiny HTTP
# server in the background that returns 200/OK.
#
# Opt-in via SQUID_TEST_SERVICE_HEALTHZ=1 — when unset (default),
# the script behaves as before (no HTTP listener). The fixture sets the
# env var via the systemd unit's Environment= directive when needed.
HEALTHZ_PID=""
if [ "${SQUID_TEST_SERVICE_HEALTHZ:-0}" = "1" ] && command -v python3 >/dev/null 2>&1; then
    HEALTHZ_PORT="${SQUID_TEST_SERVICE_HEALTHZ_PORT:-8080}"
    python3 -c "
import http.server, socketserver, sys
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'text/plain')
        self.end_headers()
        self.wfile.write(b'OK\n')
    def log_message(self, *args, **kwargs): pass
# allow_reuse_address=True is essential for Phase B re-bind. Without it,
# v1's python3 leaves port \$HEALTHZ_PORT in TIME_WAIT (60s on Linux),
# and v2's bind fails with 'Address already in use' → silent crash
# (background & swallows the exception) → healthz unresponsive →
# .sh thinks upgrade failed → rollback fires.
class ReusableServer(socketserver.TCPServer):
    allow_reuse_address = True
with ReusableServer(('127.0.0.1', $HEALTHZ_PORT), H) as httpd:
    httpd.serve_forever()
" &
    HEALTHZ_PID=$!
fi

read_version() {
    if [ -f "$VERSION_FILE" ]; then
        cat "$VERSION_FILE" | tr -d '[:space:]'
    else
        echo "unknown"
    fi
}

cleanup() {
    rm -f "$MARKER_FILE" 2>/dev/null || true
    if [ -n "$HEALTHZ_PID" ] && kill -0 "$HEALTHZ_PID" 2>/dev/null; then
        kill "$HEALTHZ_PID" 2>/dev/null || true
    fi
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
