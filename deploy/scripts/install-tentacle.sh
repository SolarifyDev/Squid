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
#   1. Env var override - SQUID_LIBC=musl|glibc  (explicit operator wins)
#   2. `ldd --version` output grep - works when ldd is installed
#      (Alpine's has `musl libc (x86_64) ...` on stderr; glibc prints
#      `ldd (GNU libc) ...` or `... GLIBC ...`)
#   3. File-existence fallback - `/lib/ld-musl-*` presence. Works on
#      minimal / distroless containers that strip ldd but keep the
#      dynamic linker itself. Catches Void + Wolfi + Chimera, which
#      D3's CI matrix can't easily smoke-test today.
# If none of the three says "musl", default to glibc - the majority case.
LIBC="${SQUID_LIBC:-}"

if [ -z "$LIBC" ]; then
    if command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl; then
        LIBC="musl"
    fi
fi

if [ -z "$LIBC" ]; then
    # File-glob fallback - compgen avoids bash-ism; a simple for-loop
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
        echo "Warning: couldn't detect package manager - install libicu manually."
        return 1
    fi
}

install_runtime_deps || true

# Resolve download URL.
# GitHub Actions publishes two tar.gz per arch under each release tag:
#   squid-tentacle-${VERSION}-${RID}.tar.gz     (versioned)
#   squid-tentacle-${RID}.tar.gz                (unversioned "latest" copy)
#
# For "latest", use GitHub's /releases/latest/download redirect - always resolves.
# For a specific version, try the tag as-given first; if that 404s, fall back to "v"-prefixed.
SQUID_BASE_URL="${SQUID_BASE_URL:-https://squid.solarifyai.com}"

# -- Optimistic package-manager install (Phase 2 Part 2 symmetry) -------------
# Try our signed APT/RPM repo BEFORE falling back to a direct GitHub Releases
# tarball download. squid.solarifyai.com is Cloudflare-fronted so CN/slow-region
# clients are 5-10x faster via the package manager than via github.com/releases.
# Matches the 3-method priority order the in-UI upgrade flow uses:
# apt → yum → tarball.
#
# Only attempted for VERSION=latest because our reprepro config keeps a single
# version per (package, arch) - `apt install squid-tentacle=1.3.8` fails if
# stable currently has 1.4.1. Explicit --version installs keep going through
# tarball which is tag-pinned on GitHub Releases (retained per-tag forever).
#
# Operator can force-skip the package-manager path with NO_PKG_INSTALL=1 to
# reproduce the exact pre-Phase-2-Part-2 behaviour (for debugging).
PKG_INSTALL_DONE=0
if [ "$VERSION" = "latest" ] && [ "${NO_PKG_INSTALL:-0}" != "1" ]; then
    if command -v apt-get >/dev/null 2>&1; then
        echo "Attempting install via Squid APT repo (${SQUID_BASE_URL}/apt) - typically faster than GitHub direct..."

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
                echo "APT install failed - falling back to tarball from GitHub Releases"
            fi
        else
            echo "Failed to fetch ${SQUID_BASE_URL}/public.key - APT repo unavailable, falling back to tarball"
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
                echo "$YUM_BIN install failed - falling back to tarball"
                rm -f /etc/yum.repos.d/squid-tentacle.repo 2>/dev/null
            fi
        fi
    fi
fi

# -- Tarball fallback - only if no package manager did the install ------------
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

ARCHIVE_PATH="$TMP_DIR/tentacle.tar.gz"

# curl options for robustness against slow cross-border routes (China ↔
# GitHub can easily run 30-60s for a fresh connection + CDN handoff):
#   --connect-timeout 15  - cap TLS handshake (separate from --max-time)
#   --max-time 300        - 5 min overall budget per attempt (65 MB tarball
#                           over a slow link can take 2-3 min)
#   --retry 3 --retry-delay 5 --retry-all-errors
#                         - retry on network errors + 4xx + 5xx (default
#                           retry only covers transient network failures)
# Stderr is NOT silenced anymore - the previous `2>/dev/null` hid the real
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

    # Extract into a versioned ("blue-green") layout: the binary lives in
    # versions/<v> and a stable `current` symlink selects the active version.
    # A later in-UI upgrade activates a new version by atomically repointing
    # `current`, so it never touches the running version's directory — any
    # upgrade failure leaves the old version intact. tar contents are flat (no
    # wrapper dir), so stage first, then name the directory by the concrete
    # version the binary reports (works for both `latest` and explicit --version).
    STAGING_DIR="$TMP_DIR/extract"
    mkdir -p "$STAGING_DIR"
    tar xzf "$ARCHIVE_PATH" -C "$STAGING_DIR"

    chmod +x "$STAGING_DIR/Squid.Tentacle"

    RESOLVED_VERSION=$(timeout 10 "$STAGING_DIR/Squid.Tentacle" version </dev/null 2>/dev/null | head -1 | tr -cd '[:alnum:]._-' || true)

    if [ -n "$RESOLVED_VERSION" ]; then
        VERSION_DIR="$INSTALL_DIR/versions/$RESOLVED_VERSION"
        mkdir -p "$INSTALL_DIR/versions"

        # Replace any prior copy of this exact version, then move the staged tree in.
        if [ -d "$VERSION_DIR" ]; then rm -rf "$VERSION_DIR"; fi
        mv "$STAGING_DIR" "$VERSION_DIR"

        # Point `current` at the new version atomically (symlink rename is atomic).
        ln -sfn "$VERSION_DIR" "$INSTALL_DIR/current.tmp"
        mv -T "$INSTALL_DIR/current.tmp" "$INSTALL_DIR/current"

        VERSIONED=1
        echo "Installed versioned layout: current -> versions/$RESOLVED_VERSION"
    else
        # Best-effort fallback: the binary couldn't report its version (e.g. missing
        # runtime deps). Extract flat so the install still completes exactly as it did
        # before the versioned layout existed; the binary-verify step below surfaces
        # any real runtime problem. Never fail an install that used to succeed.
        echo "Warning: binary did not report a version; using flat layout (no versioned upgrades)."
        mkdir -p "$INSTALL_DIR"
        cp -a "$STAGING_DIR/." "$INSTALL_DIR/"
    fi
fi

# Resolve the directory holding the runnable binaries. Versioned (tarball) installs
# run through the stable `current` pointer; package-manager (apt/yum) installs are
# flat in $INSTALL_DIR. Everything below is layout-agnostic via $BIN_DIR.
if [ "${VERSIONED:-0}" = "1" ]; then
    BIN_DIR="$INSTALL_DIR/current"
else
    BIN_DIR="$INSTALL_DIR"
fi

# Make binaries executable. Calamari is spawned by Tentacle at runtime, so it also needs +x.
chmod +x "$BIN_DIR/Squid.Tentacle"
[ -f "$BIN_DIR/squid-calamari" ] && chmod +x "$BIN_DIR/squid-calamari"

# Well-known-name symlink at the install root. Stable across upgrades — for versioned
# installs it resolves through `current`, so the path survives a version swap.
ln -sf "$BIN_DIR/Squid.Tentacle" "$INSTALL_DIR/${BINARY_NAME}"

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

# Upgrade state directory - consumed by upgrade-linux-tentacle.sh which writes
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
# curl fails with "(23) Failure writing output" - auto-rollback silently
# disabled for every upgrade.
mkdir -p "$STATE_DIR/rollback"

# Transfer ownership to the service user if one exists.
if getent passwd "$SERVICE_USER" >/dev/null 2>&1; then
    chown -R "$SERVICE_USER:$SERVICE_USER" "$CONFIG_DIR" "$WORKSPACE_DIR" "$INSTALL_DIR" "$STATE_DIR" 2>/dev/null || true
    echo "Ownership set to ${SERVICE_USER}: ${CONFIG_DIR}, ${WORKSPACE_DIR}, ${INSTALL_DIR}, ${STATE_DIR}"
fi

# -- Auto-configure APT/RPM repo for in-UI upgrade method dispatch ----------
# Phase 2 Part 2: the upgrade bash script tries `apt-get install` and
# `dnf install` BEFORE falling back to direct tarball download. For those
# methods to actually match, our Squid repo must be configured here.
# Idempotent - re-running install-tentacle.sh will overwrite with current
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
        # derived from SQUID_BASE_URL - no impact on any other repo.
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
        echo "Warning: failed to fetch ${SQUID_BASE_URL}/public.key - APT repo NOT configured."
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
        echo "Warning: failed to fetch ${SQUID_BASE_URL}/rpm/squid-tentacle.repo - RPM repo NOT configured."
        echo "  In-UI upgrades will fall back to direct tarball download (still works)."
        rm -f /etc/yum.repos.d/squid-tentacle.repo.tmp
    fi
fi

# -- Sudoers rule for the upgrade flow ----------------------------------------
# The upgrade bash script runs as the service user. It needs to escalate for:
#   1. systemd-run --scope        (the scope-detach trick from Phase 1)
#   2. package install            (via the root-owned package-install helper)
#   3. status file writes         (mkdir/chown under /var/lib/squid-tentacle)
#
# Inside the scope, bash runs as root (sudo systemd-run elevates) so further
# sudo calls there are no-ops.
#
# Two sudo implementations must accept the generated file:
#   - classic sudo (1.8.x on RHEL 7 / Ubuntu 20.04, 1.9.x elsewhere), and
#   - sudo-rs (the default sudo on Ubuntu 26.04+), whose parser REJECTS any
#     wildcard inside a command argument ("wildcards are not allowed in
#     command arguments") and only accepts a standalone trailing '*'.
# A single rejected line makes visudo reject the whole file, so the rules are
# split into a CORE block (no in-argument wildcards, accepted everywhere) and
# a LEGACY block (the pre-1.9.9 wildcard rules, kept byte-for-byte so servers
# older than this installer keep upgrading on classic sudo). CORE+LEGACY is
# installed where visudo accepts it; otherwise CORE alone.
#
# Package installs go through PACKAGE_INSTALL_HELPER rather than an
# argument-wildcarded apt-get/dnf/yum rule: the helper is root-owned, takes
# exactly (manager, version), validates the version against a strict
# character allowlist and execs the package manager with a fixed argv. Its
# sudoers rule is the bare path (no '*'), which both implementations treat
# as "any arguments" - the helper, not sudoers, does the argument check.
# The server (AptUpgradeMethod/YumUpgradeMethod) calls it only when it exists
# and is executable, so older installs keep using the LEGACY rules.
PACKAGE_INSTALL_HELPER="/usr/libexec/squid-tentacle/package-install"
if getent passwd "$SERVICE_USER" >/dev/null 2>&1 && [ -d /etc/sudoers.d ]; then
    # Root-owned dir + file: NOT under $INSTALL_DIR (chown'd to the service
    # user above) - a helper the service user could rewrite would be a root
    # shell. The quoted heredoc ('HELPER_EOF') keeps the body byte-exact.
    if install -d -o 0 -g 0 -m 0755 "$(dirname "$PACKAGE_INSTALL_HELPER")" \
        && cat > "${PACKAGE_INSTALL_HELPER}.tmp" <<'HELPER_EOF'
#!/bin/sh
# squid-tentacle package-install helper. Installed by install-tentacle.sh,
# root-owned 0755. The in-UI upgrade runs it through sudo as the service
# user, so it is the ONLY package-manager entry point that user has:
#   package-install apt|dnf|yum VERSION
# VERSION is the full package version (rpm: VERSION-RELEASE). It must start
# with a digit and contain only [A-Za-z0-9._+~-]: no spaces, newlines, '/',
# '=', ':' or globs, so it can never smuggle in a second package, a local
# .deb/.rpm path or a package-manager option.
set -eu
SAFE_PATH=/usr/sbin:/usr/bin:/sbin:/bin
PATH=$SAFE_PATH
LC_ALL=C
export PATH LC_ALL
usage() { echo "usage: package-install apt|dnf|yum VERSION" >&2; exit 64; }
[ "$#" -eq 2 ] || usage
MANAGER=$1
VERSION=$2
case "$VERSION" in
    ''|[!0-9]*|*[!A-Za-z0-9._+~-]*) echo "package-install: rejected version" >&2; exit 65 ;;
esac
[ "${#VERSION}" -le 64 ] || { echo "package-install: rejected version (too long)" >&2; exit 65; }
case "$MANAGER" in
    apt)
        env -i PATH="$SAFE_PATH" apt-get update -qq -o Dir::Etc::sourcelist=sources.list.d/squid.list -o Dir::Etc::sourceparts=- -o APT::Get::List-Cleanup=0 -o Acquire::http::Timeout=60 -o Acquire::Retries=1
        exec env -i PATH="$SAFE_PATH" DEBIAN_FRONTEND=noninteractive apt-get install -y --allow-downgrades -o Acquire::http::Timeout=120 -o Acquire::Retries=1 "squid-tentacle=$VERSION"
        ;;
    dnf|yum)
        exec env -i PATH="$SAFE_PATH" "$MANAGER" install -y "squid-tentacle-$VERSION"
        ;;
    *) usage ;;
esac
HELPER_EOF
    then
        chown 0:0 "${PACKAGE_INSTALL_HELPER}.tmp"
        chmod 0755 "${PACKAGE_INSTALL_HELPER}.tmp"
        mv -f "${PACKAGE_INSTALL_HELPER}.tmp" "$PACKAGE_INSTALL_HELPER"
        echo "Installed package-install helper: ${PACKAGE_INSTALL_HELPER}"
    else
        rm -f "${PACKAGE_INSTALL_HELPER}.tmp" 2>/dev/null || true
        echo "Warning: could not install ${PACKAGE_INSTALL_HELPER}; in-UI upgrades will fall back to the legacy sudoers rules or the tarball method."
    fi

    SUDOERS_FILE="/etc/sudoers.d/squid-tentacle-upgrade"
    cat > "${SUDOERS_FILE}.core" <<SUDOERS_CORE_EOF
# Auto-generated by install-tentacle.sh.
# Remove this file to disable in-UI upgrades (they'll prompt for password and hang).
#
# IMPORTANT: sudo matches command paths by string equality, NOT symlink
# resolution. On modern Ubuntu/Debian with usrmerge, /bin/mkdir is a symlink
# to /usr/bin/mkdir but sudo only matches whatever absolute path it resolves
# from the user's PATH. Since PATH lists /usr/bin before /bin, sudo typically
# resolves to /usr/bin/ on these systems. We list BOTH paths so the rules
# work pre- and post-usrmerge.
#
# CORE rules: no wildcard inside any command argument (sudo-rs rejects
# those) - only the standalone trailing '*' on systemd-run, which every
# supported sudo accepts.
#
# IMPORTANT: this heredoc is UNQUOTED so bash can expand SERVICE_USER and
# STATE_DIR. That same unquoted mode makes bash run command substitution on
# any backtick pair, even inside comments - do NOT use backticks anywhere
# between here and SUDOERS_CORE_EOF.

# (1) Scope detach - the single hard privilege. Everything inside the scope
# inherits root from this sudo, so post-scope ops don't need sudoers entries.
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/systemd-run --scope --collect --quiet *
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/systemd-run --scope --collect --quiet *

# (2) Package install - bare path, no arguments listed: sudo passes any
# arguments and the root-owned helper itself accepts only
# (apt|dnf|yum, strict version). Do NOT append a '*': sudo-rs before 0.2.13
# reads it as a literal argument and the rule would never match.
${SERVICE_USER} ALL=(root) NOPASSWD: ${PACKAGE_INSTALL_HELPER}

# (3) Pre-scope status file writes - write_status() runs before scope entry.
# The status file itself is moved into place without sudo (STATE_DIR is
# owned by the service user); mkdir/chown stay for hosts where it is not.
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mkdir -p ${STATE_DIR}
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mkdir -p ${STATE_DIR}
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/chown ${SERVICE_USER}\:${SERVICE_USER} ${STATE_DIR}/last-upgrade.json
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/chown ${SERVICE_USER}\:${SERVICE_USER} ${STATE_DIR}/last-upgrade.json

# (4) Auto-rollback snapshot dir (C1, 1.6.0).
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mkdir -p ${STATE_DIR}/rollback
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mkdir -p ${STATE_DIR}/rollback

# (5) dpkg lock probing (A3, 1.6.0). Phase A apt method polls
# /var/lib/dpkg/lock-frontend to detect known background updaters
# (apt-daily, unattended-upgrades) before apt install. fuser needs root
# to read the lock fd. Both /bin and /usr/bin paths for usrmerge compat.
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/fuser /var/lib/dpkg/lock-frontend
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/fuser /var/lib/dpkg/lock-frontend
SUDOERS_CORE_EOF
    cat > "${SUDOERS_FILE}.legacy" <<SUDOERS_LEGACY_EOF

# LEGACY rules - the pre-1.9.9 argument-wildcard forms, kept verbatim so a
# Squid server older than this installer (which issues these exact commands
# instead of calling the package-install helper) still upgrades this host.
# Installed only where visudo accepts them (classic sudo); sudo-rs rejects
# in-argument wildcards, so there this block is dropped and CORE stands
# alone. Do NOT use backticks between here and SUDOERS_LEGACY_EOF (unquoted
# heredoc - see the CORE block note).
#
# Colon escape: the \:\: below maps to literal :: in the compiled rule.
# sudoers treats : as a field separator, rejecting an un-escaped :: with
# a syntax error at the second colon; \: is the documented escape.
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get update
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get update -qq
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get update -qq -o Dir\:\:Etc\:\:sourcelist=sources.list.d/squid.list -o Dir\:\:Etc\:\:sourceparts=- -o APT\:\:Get\:\:List-Cleanup=0 -o Acquire\:\:http\:\:Timeout=60 -o Acquire\:\:Retries=1
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get install -y --allow-downgrades squid-tentacle=*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/apt-get install -y --allow-downgrades -o Acquire\:\:http\:\:Timeout=120 -o Acquire\:\:Retries=1 squid-tentacle=*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/dnf install -y squid-tentacle-*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/yum install -y squid-tentacle-*
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mv /tmp/squid-upgrade-status.* ${STATE_DIR}/last-upgrade.json
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mv /tmp/squid-upgrade-status.* ${STATE_DIR}/last-upgrade.json
${SERVICE_USER} ALL=(root) NOPASSWD: /bin/mv /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb.tmp /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/mv /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb.tmp /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/dpkg -i --force-downgrade /var/lib/squid-tentacle/rollback/squid-tentacle_*.deb
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/dnf downgrade -y squid-tentacle-*
${SERVICE_USER} ALL=(root) NOPASSWD: /usr/bin/yum downgrade -y squid-tentacle-*
SUDOERS_LEGACY_EOF
    cat "${SUDOERS_FILE}.core" "${SUDOERS_FILE}.legacy" > "${SUDOERS_FILE}.tmp"
    # visudo -c validates the file; if it rejects, we skip install rather
    # than corrupt sudoers (a broken sudoers locks out root on strict configs).
    # Capture stderr so operators can diagnose a template bug (don't swallow
    # silently - that hid a `::` escape bug for an entire release cycle).
    # `if VAR=$(...); then` is the ONLY form that exempts the assignment from
    # `set -e` - a plain `VAR=$(failing); if [ $? ...` aborts the script.
    SUDOERS_INSTALLED=""
    if VISUDO_OUTPUT=$(visudo -c -f "${SUDOERS_FILE}.tmp" 2>&1); then
        SUDOERS_INSTALLED="core+legacy"
    elif VISUDO_CORE_OUTPUT=$(visudo -c -f "${SUDOERS_FILE}.core" 2>&1); then
        cp "${SUDOERS_FILE}.core" "${SUDOERS_FILE}.tmp"
        SUDOERS_INSTALLED="core"
        echo "Note: this sudo rejects the legacy wildcard sudoers rules (expected with sudo-rs); installing the core rules only."
        echo "  Servers older than this installer will fall back to the tarball upgrade method on this host."
    fi
    if [ -n "$SUDOERS_INSTALLED" ]; then
        chmod 440 "${SUDOERS_FILE}.tmp"
        mv "${SUDOERS_FILE}.tmp" "${SUDOERS_FILE}"
        echo "Installed upgrade sudoers rule (${SUDOERS_INSTALLED}): ${SUDOERS_FILE}"
        # visudo only proves the file PARSES. Some sudo versions parse a rule
        # they then never match (sudo-rs before 0.2.13 reads the trailing '*'
        # on systemd-run literally), so ask sudo itself, as the service user.
        # sudo -l needs the command to exist, so systemd-run is only probed
        # where it is installed.
        probe_sudo_grant() {
            su -s /bin/sh "$SERVICE_USER" -c "sudo -n -l $1" </dev/null >/dev/null 2>&1 \
                || echo "Warning: sudo does not grant '$1' to $SERVICE_USER; in-UI upgrades may not work on this host."
        }
        probe_sudo_grant "$PACKAGE_INSTALL_HELPER apt 1.0.0"
        SCOPE_BIN=$(command -v systemd-run || true)
        [ -z "$SCOPE_BIN" ] || probe_sudo_grant "$SCOPE_BIN --scope --collect --quiet /bin/true"
    else
        echo "Warning: generated sudoers rule failed validation; NOT installed. In-UI upgrades will prompt for password and hang."
        echo "  visudo output:"
        echo "${VISUDO_OUTPUT}" | sed 's/^/    /'
        echo "${VISUDO_CORE_OUTPUT:-}" | sed 's/^/    /'
    fi
    rm -f "${SUDOERS_FILE}.tmp" "${SUDOERS_FILE}.core" "${SUDOERS_FILE}.legacy"
fi

# Verify binary is runnable (use the `help` subcommand - `--help` is routed to RunCommand in Program.cs).
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
# `sudo` on register is required - without it the persisted config lands in
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
echo "    --flavor Tentacle"
echo "  sudo squid-tentacle service install"
