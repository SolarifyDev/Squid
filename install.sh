#!/bin/bash
set -euo pipefail

# Squid Tentacle Linux Installer
# Usage: curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | sudo bash
# Or:    sudo bash install-tentacle.sh [--version 1.2.7] [--install-dir /opt/squid-tentacle]

BINARY_NAME="squid-tentacle"
VERSION="${TENTACLE_VERSION:-latest}"
INSTALL_DIR="${INSTALL_DIR:-/opt/squid-tentacle}"
DOWNLOAD_BASE="${DOWNLOAD_BASE:-https://github.com/SolarifyDev/Squid/releases}"

# Parse args
while [[ $# -gt 0 ]]; do
    case $1 in
        --version) VERSION="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# Detect architecture
ARCH=$(uname -m)
case $ARCH in
    x86_64)  RID_ARCH="x64" ;;
    aarch64) RID_ARCH="arm64" ;;
    arm64)   RID_ARCH="arm64" ;;
    *) echo "Error: Unsupported architecture: $ARCH"; exit 1 ;;
esac

# D2 (1.6.0): detect libc flavour so we pick the right self-contained
# .NET build. Alpine / Void / Chimera / Wolfi all ship musl-libc; a
# glibc-targeted `linux-x64` binary crashes at startup on any of them
# with cryptic "Error relocating ...: symbol not found" messages.
# `linux-musl-x64` is .NET's officially-supported RID for musl builds.
#
# Three-stage detection (audit D.4 fix, 1.6.x):
#   1. Env var override — SQUID_LIBC=musl|glibc  (explicit operator wins)
#   2. `ldd --version` output grep — works when ldd is installed
#      (Alpine's has `musl libc (x86_64) ...` on stderr; glibc prints
#      `ldd (GNU libc) ...` or `... GLIBC ...`)
#   3. File-existence fallback — `/lib/ld-musl-*` presence. Works on
#      minimal / distroless containers that strip ldd but keep the
#      dynamic linker itself. Catches Void + Wolfi + Chimera, which
#      D3's CI matrix can't easily smoke-test today.
# If none of the three says "musl", default to glibc — the majority case.
LIBC="${SQUID_LIBC:-}"

if [ -z "$LIBC" ]; then
    if command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; then
        LIBC="musl"
    fi
fi

if [ -z "$LIBC" ]; then
    # File-glob fallback — compgen avoids bash-ism; a simple for-loop
    # over /lib/ld-musl-* works in POSIX sh too. If any match exists,
    # we're on musl.
    for ld_musl in /lib/ld-musl-* /usr/lib/ld-musl-*; do
        if [ -e "$ld_musl" ]; then
            LIBC="musl"
            break
        fi
    done
fi

if [ -z "$LIBC" ]; then
    LIBC="glibc"
fi

if [ "$LIBC" = "musl" ]; then
    RID="linux-musl-${RID_ARCH}"
else
    RID="linux-${RID_ARCH}"
fi

# Require root for default install dir
if [ "$INSTALL_DIR" = "/opt/squid-tentacle" ] && [ "$(id -u)" -ne 0 ]; then
    echo "Error: Root privileges required. Run with:"
    echo "  curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | sudo bash"
    echo ""
    echo "Or install to a user directory:"
    echo "  curl ... | INSTALL_DIR=\$HOME/.squid-tentacle bash"
    exit 1
fi

echo "=== Squid Tentacle Installer ==="
echo "Version:  ${VERSION}"
echo "Arch:     ${RID}"
echo "Install:  ${INSTALL_DIR}"
echo ""

# Install runtime dependencies (.NET 9 self-contained needs libicu + ca-certificates).
# Detect the host package manager and install silently if libicu is missing.
install_runtime_deps() {
    if ldconfig -p 2>/dev/null | grep -q "libicuuc"; then
        return 0
    fi

    echo "Installing runtime dependencies (libicu, ca-certificates)..."

    if command -v apt-get >/dev/null 2>&1; then
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq >/dev/null 2>&1 || true
        apt-get install -y -qq libicu-dev ca-certificates >/dev/null 2>&1
    elif command -v dnf >/dev/null 2>&1; then
        dnf install -y -q libicu ca-certificates >/dev/null
    elif command -v yum >/dev/null 2>&1; then
        yum install -y -q libicu ca-certificates >/dev/null
    elif command -v apk >/dev/null 2>&1; then
        apk add --no-cache --quiet icu-libs ca-certificates
    else
        echo "Warning: couldn't detect package manager — install libicu manually."
        return 1
    fi
}

install_runtime_deps || true

# Resolve download URL.
# GitHub Actions publishes two tar.gz per arch under each release tag:
#   squid-tentacle-${VERSION}-${RID}.tar.gz     (versioned)
#   squid-tentacle-${RID}.tar.gz                (unversioned "latest" copy)
#
# For "latest", use GitHub's /releases/latest/download redirect — always resolves.
# For a specific version, try the tag as-given first; if that 404s, fall back to "v"-prefixed.
SQUID_BASE_URL="${SQUID_BASE_URL:-https://squid.solarifyai.com}"

# ── Optimistic package-manager install (Phase 2 Part 2 symmetry) ─────────────
# Try our signed APT/RPM repo BEFORE falling back to a direct GitHub Releases
# tarball download. squid.solarifyai.com is Cloudflare-fronted so CN/slow-region
# clients are 5-10x faster via the package manager than via github.com/releases.
# Matches the 3-method priority order the in-UI upgrade flow uses:
# apt → yum → tarball.
#
# Only attempted for VERSION=latest because our reprepro config keeps a single
# version per (package, arch) — `apt install squid-tentacle=1.3.8` fails if
# stable currently has 1.4.1. Explicit --version installs keep going through
# tarball which is tag-pinned on GitHub Releases (retained per-tag forever).
#
# Operator can force-skip the package-manager path with NO_PKG_INSTALL=1 to
# reproduce the exact pre-Phase-2-Part-2 behaviour (for debugging).
PKG_INSTALL_DONE=0
if [ "$VERSION" = "latest" ] && [ "${NO_PKG_INSTALL:-0}" != "1" ]; then
    if command -v apt-get >/dev/null 2>&1; then
        echo "Attempting install via Squid APT repo (${SQUID_BASE_URL}/apt) — typically faster than GitHub direct..."

        install -m 0755 -d /etc/apt/keyrings
        if curl -fL --connect-timeout 15 --max-time 60 "${SQUID_BASE_URL}/public.key" 2>/dev/null \
             | gpg --dearmor -o /etc/apt/keyrings/squid.gpg 2>/dev/null; then
            chmod a+r /etc/apt/keyrings/squid.gpg
            ARCH_DEB=$(dpkg --print-architecture 2>/dev/null || echo amd64)
            echo "deb [arch=${ARCH_DEB} signed-by=/etc/apt/keyrings/squid.gpg] ${SQUID_BASE_URL}/apt stable main" \
                > /etc/apt/sources.list.d/squid.list

            if apt-get update -qq && \
               DEBIAN_FRONTEND=noninteractive apt-get install -y ${BINARY_NAME}; then
                echo "Installed ${BINARY_NAME} via APT"
                PKG_INSTALL_DONE=1
            else
                echo "APT install failed — falling back to tarball from GitHub Releases"
            fi
        else
            echo "Failed to fetch ${SQUID_BASE_URL}/public.key — APT repo unavailable, falling back to tarball"
            rm -f /etc/apt/sources.list.d/squid.list /etc/apt/keyrings/squid.gpg 2>/dev/null
        fi
    elif command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1; then
        echo "Attempting install via Squid YUM repo (${SQUID_BASE_URL}/rpm)..."

        if curl -fL --connect-timeout 15 --max-time 60 "${SQUID_BASE_URL}/rpm/squid-tentacle.repo" \
             -o /etc/yum.repos.d/squid-tentacle.repo 2>/dev/null; then
            YUM_BIN=$(command -v dnf || command -v yum)
            if $YUM_BIN install -y ${BINARY_NAME}; then
                echo "Installed ${BINARY_NAME} via $YUM_BIN"
                PKG_INSTALL_DONE=1
            else
                echo "$YUM_BIN install failed — falling back to tarball"
                rm -f /etc/yum.repos.d/squid-tentacle.repo 2>/dev/null
            fi
        fi
    fi
fi

# ── Tarball fallback — only if no package manager did the install ────────────
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

ARCHIVE_PATH="$TMP_DIR/tentacle.tar.gz"

# curl options for robustness against slow cross-border routes (China ↔
# GitHub can easily run 30-60s for a fresh connection + CDN handoff):
#   --connect-timeout 15  — cap TLS handshake (separate from --max-time)
#   --max-time 300        — 5 min overall budget per attempt (65 MB tarball
#                           over a slow link can take 2-3 min)
#   --retry 3 --retry-delay 5 --retry-all-errors
#                         — retry on network errors + 4xx + 5xx (default
#                           retry only covers transient network failures)
# Stderr is NOT silenced anymore — the previous `2>/dev/null` hid the real
# reason for failure (TLS timeout, DNS, cert, ...) behind a generic
# "Failed to download" message.
download_ok() {
    # --progress-bar: single clean line of ######### rather than the
    # default scrolling stats table (which is noisy in CI logs but
    # useless in interactive shell where we actually want to see
    # "am I hung, or just slow?").
    curl -fL --progress-bar \
         --connect-timeout 15 --max-time 300 \
         --retry 3 --retry-delay 5 --retry-all-errors \
         "$1" -o "$ARCHIVE_PATH"
}

if [ "$PKG_INSTALL_DONE" != "1" ]; then
    if [ "$VERSION" = "latest" ]; then
        URL="${DOWNLOAD_BASE}/latest/download/${BINARY_NAME}-${RID}.tar.gz"
        echo "Downloading from ${URL}..."

        if ! download_ok "$URL"; then
            echo ""
            echo "Error: Failed to download latest release from ${URL}"
            echo "Possible causes (in order of likelihood):"
            echo "  1. GitHub + CDN slow from this region (China) → retry or pin --version X.Y.Z for a specific tag"
            echo "  2. No 'latest' release exists (check ${DOWNLOAD_BASE}/latest)"
            echo "  3. Outbound HTTPS to github.com blocked by firewall/proxy"
            echo "  4. For air-gapped installs, set DOWNLOAD_BASE to a private mirror"
            exit 1
        fi
    else
        # Try both tag formats: plain version (e.g. 1.2.7) and v-prefixed (e.g. v1.2.7).
        URL_PLAIN="${DOWNLOAD_BASE}/download/${VERSION}/${BINARY_NAME}-${VERSION}-${RID}.tar.gz"
        URL_V_PREFIXED="${DOWNLOAD_BASE}/download/v${VERSION}/${BINARY_NAME}-${VERSION}-${RID}.tar.gz"

        echo "Downloading from ${URL_PLAIN}..."

        if ! download_ok "$URL_PLAIN"; then
            echo "Tag '${VERSION}' not found (or network error), retrying with 'v${VERSION}'..."
            echo "Downloading from ${URL_V_PREFIXED}..."

            if ! download_ok "$URL_V_PREFIXED"; then
                echo ""
                echo "Error: Could not download ${VERSION} from either tag form."
                echo "  Tried: ${URL_PLAIN}"
                echo "  Tried: ${URL_V_PREFIXED}"
                echo "  Releases list: ${DOWNLOAD_BASE}"
                exit 1
            fi
        fi
    fi

    # Extract (tar contents are flat — no wrapper dir)
    mkdir -p "$INSTALL_DIR"
    tar xzf "$ARCHIVE_PATH" -C "$INSTALL_DIR"
fi

# Make binaries executable. Calamari is spawned by Tentacle at runtime, so it also needs +x.
chmod +x "$INSTALL_DIR/Squid.Tentacle"
[ -f "$INSTALL_DIR/Squid.Calamari" ] && chmod +x "$INSTALL_DIR/Squid.Calamari"

# Create well-known-name symlink inside install dir
ln -sf "$INSTALL_DIR/Squid.Tentacle" "$INSTALL_DIR/${BINARY_NAME}"

# Expose on PATH
if [ -d /usr/local/bin ]; then
    ln -sf "$INSTALL_DIR/${BINARY_NAME}" /usr/local/bin/${BINARY_NAME}
    echo "Installed: /usr/local/bin/${BINARY_NAME}"
else
    echo "Add to PATH: export PATH=\"${INSTALL_DIR}:\$PATH\""
fi

# Create a dedicated non-login system user that owns cert/config/workspace dirs.
# The systemd unit can then run with User=${SERVICE_USER} instead of root, so a
# compromised script payload can't pivot to root on the host. Skipped when
# --no-user is passed (for dev/test or when an operator wants to manage the
# identity themselves).
SERVICE_USER="${SERVICE_USER:-squid-tentacle}"
if [ "${CREATE_USER:-yes}" = "yes" ]; then
    if ! getent passwd "$SERVICE_USER" >/dev/null 2>&1; then
        if command -v useradd >/dev/null 2>&1; then
            useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER" 2>/dev/null || true
            echo "Created system user: ${SERVICE_USER}"
        elif command -v adduser >/dev/null 2>&1; then
            adduser --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER" 2>/dev/null || true
            echo "Created system user: ${SERVICE_USER}"
        fi
    fi
fi

# Create the system-scope config directory for multi-instance support.
# Register + run will read/write here; restricting to owner-only protects the
# API key and server thumbprint that get persisted after a successful `register`.
CONFIG_DIR="/etc/squid-tentacle"
if [ ! -d "$CONFIG_DIR" ]; then
    mkdir -p "$CONFIG_DIR/instances"
    chmod 700 "$CONFIG_DIR"
    echo "Created config dir: ${CONFIG_DIR}"
fi

# Workspace for script staging. Created upfront so the service user owns it
# (otherwise the first script run does it under root and permissions drift).
WORKSPACE_DIR="${WORKSPACE_DIR:-/squid/work}"
mkdir -p "$WORKSPACE_DIR"

# Upgrade state directory — consumed by upgrade-linux-tentacle.sh which writes
# /var/lib/squid-tentacle/last-upgrade.json on every upgrade attempt. Server
# reads this file on the next health check to learn the upgrade's outcome
# after the Halibut connection dies mid-restart. Pre-creating with correct
# owner avoids a sudo dance in the upgrade script and matches Octopus's
# "exit-code-to-disk" out-of-band reporting pattern.
STATE_DIR="/var/lib/squid-tentacle"
mkdir -p "$STATE_DIR"
chmod 755 "$STATE_DIR"

# Auto-rollback snapshot dir (C1+C2, 1.6.0). Phase A's apt method writes the
# previous-version .deb here as a best-effort rollback snapshot; Phase B's
# failure path runs `dpkg -i --force-downgrade` from this dir. The curl that
# downloads the snapshot runs AS $SERVICE_USER (non-root), so the directory
# MUST be writable by that user. If we skip this and let the runtime sudo
# `mkdir -p` create it on first upgrade, the dir ends up root:root 0755 and
# curl fails with "(23) Failure writing output" — auto-rollback silently
# disabled for every upgrade.
mkdir -p "$STATE_DIR/rollback"

# Transfer ownership to the service user if one exists.
if getent passwd "$SERVICE_USER" >/dev/null 2>&1; then
    chown -R "$SERVICE_USER:$SERVICE_USER" "$CONFIG_DIR" "$WORKSPACE_DIR" "$INSTALL_DIR" "$STATE_DIR" 2>/dev/null || true
    echo "Ownership set to ${SERVICE_USER}: ${CONFIG_DIR}, ${WORKSPACE_DIR}, ${INSTALL_DIR}, ${STATE_DIR}"
fi

# ── Auto-configure APT/RPM repo for in-UI upgrade method dispatch ──────────
# Phase 2 Part 2: the upgrade bash script tries `apt-get install` and
# `dnf install` BEFORE falling back to direct tarball download. For those
# methods to actually match, our Squid repo must be configured here.
# Idempotent — re-running install-tentacle.sh will overwrite with current
# repo state, picking up GPG key rotations etc.
SQUID_BASE_URL="${SQUID_BASE_URL:-https://squid.solarifyai.com}"

if command -v apt-get >/dev/null 2>&1; then
    install -m 0755 -d /etc/apt/keyrings
    if curl -fsSL --max-time 30 "${SQUID_BASE_URL}/public.key" \
         | gpg --dearmor -o /etc/apt/keyrings/squid.gpg.tmp 2>/dev/null \
         && mv /etc/apt/keyrings/squid.gpg.tmp /etc/apt/keyrings/squid.gpg; then
        chmod a+r /etc/apt/keyrings/squid.gpg
        ARCH_DEB=$(dpkg --print-architecture 2>/dev/null || echo amd64)
        echo "deb [arch=${ARCH_DEB} signed-by=/etc/apt/keyrings/squid.gpg] ${SQUID_BASE_URL}/apt stable main" \
            > /etc/apt/sources.list.d/squid.list
        echo "Configured Squid APT repo: /etc/apt/sources.list.d/squid.list"

        # Force DIRECT connection for the Squid host only. Motivation:
        # users running transparent proxies (v2raya, clash, etc.) have seen
        # apt-get slow to ~90 KB/s while raw curl to the same host pulls at
        # 1.5+ MB/s. apt respects Acquire::http::Proxy system-wide, so the
        # proxy grabs every request. Scope the override to the exact host
        # derived from SQUID_BASE_URL — no impact on any other repo.
        SQUID_HOST=$(echo "${SQUID_BASE_URL}" | sed -E 's,^https?://([^/]+).*,\1,')
        cat > /etc/apt/apt.conf.d/99-squid-direct.conf <<APT_EOF
// Auto-generated by install-tentacle.sh. Forces DIRECT for the Squid APT
// host regardless of any system-wide proxy. Fixes slow apt-get downloads
// on hosts running transparent proxies (e.g. v2raya). Safe to delete.
Acquire::http::Proxy::${SQUID_HOST} "DIRECT";
Acquire::https::Proxy::${SQUID_HOST} "DIRECT";
APT_EOF
        echo "Configured proxy bypass: /etc/apt/apt.conf.d/99-squid-direct.conf (${SQUID_HOST} → DIRECT)"
    else
        echo "Warning: failed to fetch ${SQUID_BASE_URL}/public.key — APT repo NOT configured."
        echo "  In-UI upgrades will fall back to direct tarball download (still works)."
        rm -f /etc/apt/keyrings/squid.gpg.tmp
    fi
fi

if command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1; then
    if curl -fsSL --max-time 30 "${SQUID_BASE_URL}/rpm/squid-tentacle.repo" \
         -o /etc/yum.repos.d/squid-tentacle.repo.tmp; then
        mv /etc/yum.repos.d/squid-tentacle.repo.tmp /etc/yum.repos.d/squid-tentacle.repo
        echo "Configured Squid RPM repo: /etc/yum.repos.d/squid-tentacle.repo"
    else
        echo "Warning: failed to fetch ${SQUID_BASE_URL}/rpm/squid-tentacle.repo — RPM repo NOT configured."
        echo "  In-UI upgrades will fall back to direct tarball download (still works)."
        rm -f /etc/yum.repos.d/squid-tentacle.repo.tmp
    fi
fi

# ── Sudoers rule for the upgrade flow ────────────────────────────────────────
# The upgrade bash script runs as the service user. It needs to escalate for:
#   1. systemd-run --scope        (the scope-detach trick from Phase 1)
#   2. apt-get install / dnf install   (Phase 2 Part 2 method dispatch)
#   3. status file writes         (mkdir/mv/chown under /var/lib/squid-tentacle)
#
# Inside the scope, bash runs as root (sudo systemd-run elevates) so further
# sudo calls there are no-ops. Sudoers stays narrow: package name pinned to
# 'squid-tentacle', no blanket `systemctl` or arbitrary `mv` / `apt install`.
if getent passwd "$SERVICE_USER" >/dev/null 2>&1 && [ -d /etc/sudoers.d ]; then
    SUDOERS_FILE="/etc/sudoers.d/squid-tentacle-upgrade"
    cat > "${SUDOERS_FILE}.tmp" <<SUDOERS_EOF
# Auto-generated by install-tentacle.sh.
# Remove this file to disable in-UI upgrades (they'll prompt for password and hang).
#
# IMPORTANT: sudo matches command paths by string equality, NOT symlink
# resolution. On modern Ubuntu/Debian with usrmerge, /bin/mkdir is a symlink
# to /usr/bin/mkdir but sudo only matches whatever absolute path it resolves
# from the user's PATH. Since PATH lists /usr/bin before /bin, sudo typically
# resolves to /usr/bin/ on these systems. We list BOTH paths so the rules
# work pre- and post-usrmerge.

# (1) Scope detach — the single hard privilege. Everything inside the scope
# inherits root from this sudo, so post-scope ops don't need sudoers entries.
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/systemd-run --scope --collect --quiet *
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/systemd-run --scope --collect --quiet *

# (2) Phase 2 Part 2 — package-manager install methods. Pinned to package
# name 'squid-tentacle' to prevent the service user installing anything else.
# The targeted apt-get update form is the preferred one (scoped to our
# source file only, immune to broken third-party repos). The plain forms
# stay as fallback for older agent scripts that still issue them.
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get update
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get update -qq
# IMPORTANT: this heredoc is UNQUOTED (<<SUDOERS_EOF not <<'SUDOERS_EOF') so
# bash can expand \${SERVICE_USER} / \${STATE_DIR}. That same unquoted mode
# makes bash interpret backticks as command substitution inside comments.
# Do NOT use backticks anywhere between here and SUDOERS_EOF — use plain
# text only. A rogue backtick sequence (e.g. a subshell of apt-get update)
# will inject its output into the generated sudoers file mid-stream,
# producing a syntactically invalid file and silently disabling upgrades.
# Colon escape: the \:\: below maps to literal :: in the compiled rule.
# sudoers treats : as a field separator, rejecting an un-escaped :: with
# a syntax error at the second colon; \: is the documented escape.
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get update -qq -o Dir\:\:Etc\:\:sourcelist=sources.list.d/squid.list -o Dir\:\:Etc\:\:sourceparts=- -o APT\:\:Get\:\:List-Cleanup=0 -o Acquire\:\:http\:\:Timeout=60 -o Acquire\:\:Retries=1
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get install -y --allow-downgrades squid-tentacle=*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get install -y --allow-downgrades -o Acquire\:\:http\:\:Timeout=120 -o Acquire\:\:Retries=1 squid-tentacle=*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/dnf install -y squid-tentacle-*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/yum install -y squid-tentacle-*

# (3) Pre-scope status file writes — write_status() helper runs before scope
# entry so it can log IN_PROGRESS / FAILED before the scope even starts.
# Both /bin and /usr/bin paths listed for usrmerge compat (see note above).
# The mv source path is fixed to /tmp/squid-upgrade-status.* by the bash
# helper — deterministic prefix so this rule matches exactly.
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mkdir -p ${STATE_DIR}
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mkdir -p ${STATE_DIR}
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mv /tmp/squid-upgrade-status.* ${STATE_DIR}/last-upgrade.json
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mv /tmp/squid-upgrade-status.* ${STATE_DIR}/last-upgrade.json
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/chown ${SERVICE_USER}\:${SERVICE_USER} ${STATE_DIR}/last-upgrade.json
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/chown ${SERVICE_USER}\:${SERVICE_USER} ${STATE_DIR}/last-upgrade.json

# (4) Auto-rollback support (C1+C2, 1.6.0). Phase A apt method downloads
# the previous version's .deb to ${STATE_DIR}/rollback/ as a snapshot;
# Phase B failure path runs `dpkg -i --force-downgrade snapshot.deb` to
# restore. Path is wildcarded (squid-tentacle_*_*.deb) but locked to
# our state dir so the service user can't dpkg -i an arbitrary file.
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mkdir -p ${STATE_DIR}/rollback
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mkdir -p ${STATE_DIR}/rollback
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mv /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb.tmp /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mv /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb.tmp /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/dpkg -i --force-downgrade /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb

# (5) yum auto-rollback (C3, 1.6.0). dnf/yum downgrade natively restores
# the previous RPM version (createrepo_c keeps the last 5 indexed; our
# publish workflow respects that). Pinned to package name 'squid-tentacle'
# with a wildcard suffix for the version-release pair.
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/dnf downgrade -y squid-tentacle-*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/yum downgrade -y squid-tentacle-*

# (6) dpkg lock probing (A3, 1.6.0). Phase A apt method polls
# /var/lib/dpkg/lock-frontend to detect known background updaters
# (apt-daily, unattended-upgrades) before apt install. fuser needs root
# to read the lock fd. Both /bin and /usr/bin paths for usrmerge compat.
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/fuser /var/lib/dpkg/lock-frontend
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/fuser /var/lib/dpkg/lock-frontend
SUDOERS_EOF
    # visudo -c validates the file; if it rejects, we skip install rather
    # than corrupt sudoers (a broken sudoers locks out root on strict configs).
    # Capture stderr so operators can diagnose a template bug (don't swallow
    # silently — that hid a `::` escape bug for an entire release cycle).
    # `if VAR=$(...); then` is the ONLY form that exempts the assignment from
    # `set -e` — a plain `VAR=$(failing); if [ $? ...` aborts the script.
    if VISUDO_OUTPUT=$(visudo -c -f "${SUDOERS_FILE}.tmp" 2>&1); then
        chmod 440 "${SUDOERS_FILE}.tmp"
        mv "${SUDOERS_FILE}.tmp" "${SUDOERS_FILE}"
        echo "Installed upgrade sudoers rule: ${SUDOERS_FILE}"
    else
        echo "Warning: generated sudoers rule failed validation; NOT installed. In-UI upgrades will prompt for password and hang."
        echo "  visudo output:"
        echo "${VISUDO_OUTPUT}" | sed 's/^/    /'
        rm -f "${SUDOERS_FILE}.tmp"
    fi
fi

# Verify binary is runnable (use the `help` subcommand — `--help` is routed to RunCommand in Program.cs).
if command -v ${BINARY_NAME} >/dev/null 2>&1; then
    if ${BINARY_NAME} help >/dev/null 2>&1; then
        echo "Verified: ${BINARY_NAME} executable"
    else
        echo "Warning: ${BINARY_NAME} installed but did not run successfully."
        echo "  Check glibc version (.NET 9 self-contained requires glibc 2.31+):"
        echo "    ldd --version | head -1"
    fi
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "Next steps:"
# `sudo` on register is required — without it the persisted config lands in
# ~/.config/squid-tentacle/... (invoking user's home), but the systemd unit
# runs as the dedicated squid-tentacle system user, which reads from
# /etc/squid-tentacle/... Mismatch → service crash-loops on
# UnauthorizedAccessException. Register as root → config in /etc → post-register
# chown hands it to the service user.
echo "  sudo squid-tentacle register \\"
echo "    --server https://your-squid-server:7078 \\"
echo "    --api-key API-XXXX \\"
echo "    --role web-server \\"
echo "    --environment Production \\"
echo "    --flavor LinuxTentacle"
echo "  sudo squid-tentacle service install"
