#!/bin/bash
# Runs as root inside a throwaway container (see SudoersCompatibilityE2ETests).
# Feeds the REAL installer's sudoers + helper block to the container's REAL
# sudo, then runs the REAL server-rendered apt/yum snippets as the service user.
# Package managers and systemd-run are root-owned argv-recording stubs, so the
# test proves what sudo lets through without touching any package repo.
#
#   $1 = sudo flavour: default | ws (classic sudo.ws on sudo-rs hosts) | cargo (sudo-rs on classic hosts)
#
# /seam-in (read-only mount) holds install-tentacle.sh, upgrade-linux-tentacle.sh,
# apt-snippet.sh, yum-snippet.sh; they are copied to /seam so the service user
# can read them. Every result is printed as "RESULT key=value".
set -u

mkdir -p /seam && cp /seam-in/* /seam/ && chmod 755 /seam && chmod 644 /seam/*

FLAVOUR="${1:-default}"
SEAM_BIN=/opt/seam-bin
mkdir -p "$SEAM_BIN"
case "$FLAVOUR" in
    ws)    ln -sf /usr/bin/sudo.ws "$SEAM_BIN/sudo"; ln -sf /usr/sbin/visudo.ws "$SEAM_BIN/visudo" ;;
    cargo) ln -sf /usr/lib/cargo/bin/sudo "$SEAM_BIN/sudo"; ln -sf /usr/lib/cargo/bin/visudo "$SEAM_BIN/visudo" ;;
esac
SEAM_PATH="$SEAM_BIN:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
export PATH="$SEAM_PATH"

USER_NAME=squid-tentacle
STATE=/var/lib/squid-tentacle
HELPER=/usr/libexec/squid-tentacle/package-install
STUB_LOG=/tmp/pkg-stub.log

result() { echo "RESULT $1=$2"; }

result sudo_version "$(sudo -V 2>/dev/null | head -1)"

# ── Host shaped like install-tentacle.sh leaves it ──────────────────────────
useradd -r -M -s /sbin/nologin "$USER_NAME" 2>/dev/null || useradd -r -M "$USER_NAME"
mkdir -p "$STATE/rollback"
chown -R "$USER_NAME:$USER_NAME" "$STATE"
mkdir -p /etc/apt/sources.list.d /etc/yum.repos.d /etc/sudoers.d
touch /etc/apt/sources.list.d/squid.list /etc/yum.repos.d/squid-tentacle.repo

: > "$STUB_LOG"; chmod 666 "$STUB_LOG"
for bin in apt-get dnf yum systemd-run; do
    rm -f "/usr/bin/$bin"
    printf '#!/bin/sh\nprintf "%%s" "%s" >> %s\nfor a in "$@"; do printf "[%%s]" "$a" >> %s; done\necho >> %s\n' \
        "$bin" "$STUB_LOG" "$STUB_LOG" "$STUB_LOG" > "/usr/bin/$bin"
    chmod 755 "/usr/bin/$bin"
done

# ── Producer: the installer's real sudoers + helper block ───────────────────
sed -n '/^# -- Sudoers rule for the upgrade flow/,/^# Verify binary is runnable/p' /seam/install-tentacle.sh > /tmp/install-block.sh
INSTALL_OUT=$(SERVICE_USER="$USER_NAME" STATE_DIR="$STATE" bash -euo pipefail /tmp/install-block.sh 2>&1)
INSTALL_RC=$?
echo "$INSTALL_OUT" | sed 's/^/[installer] /'
result installer_rc "$INSTALL_RC"
result installed_block "$(echo "$INSTALL_OUT" | sed -n 's/^Installed upgrade sudoers rule (\(.*\)):.*/\1/p')"
result helper_stat "$(stat -c '%U:%G:%a' "$HELPER" 2>/dev/null)"
result helper_dir_stat "$(stat -c '%U:%G:%a' "$(dirname "$HELPER")" 2>/dev/null)"
result installer_warned_scope "$(echo "$INSTALL_OUT" | grep -c "does not grant '/usr/bin/systemd-run")"
result installer_warned_helper "$(echo "$INSTALL_OUT" | grep -c "does not grant '$HELPER")"

as_user() { timeout 60 su -s /bin/bash "$USER_NAME" -c "export PATH='$SEAM_PATH'; $1" </dev/null 2>&1; }

# ── Consumer: the server-rendered snippets, run as the service user ─────────
run_snippet() {
    : > "$STUB_LOG"
    as_user "set -euo pipefail; INSTALL_OK=0; INSTALL_METHOD=''; OLD_VERSION_APT=''; OLD_VERSION_RPM=''; . $1; echo SNIPPET_INSTALL_OK=\$INSTALL_OK" \
        | sed 's/^/[snippet] /' | tee /tmp/snippet.out
    result "$2" "$(sed -n 's/^\[snippet\] SNIPPET_INSTALL_OK=//p' /tmp/snippet.out)"
    result "$2_argv" "$(tr '\n' '|' < "$STUB_LOG")"
}

run_snippet /seam/apt-snippet.sh new_apt
run_snippet /seam/yum-snippet.sh new_yum

# Old server against this install = the same snippets with the helper absent
# (the server only takes the helper path when [ -x helper ]), so the legacy
# commands, byte-identical to pre-1.9.9 servers, are what reach sudo.
mv "$HELPER" "$HELPER.away"
run_snippet /seam/apt-snippet.sh legacy_apt
run_snippet /seam/yum-snippet.sh legacy_yum
mv "$HELPER.away" "$HELPER"

# ── Least privilege ──────────────────────────────────────────────────────────
probe() { : > "$STUB_LOG"; as_user "$2; echo PROBE_RC=\$?" | tee /tmp/probe.out | sed 's/^/[probe] /'; result "$1" "$(sed -n 's/^PROBE_RC=//p' /tmp/probe.out)"; result "$1_ran" "$(wc -l < "$STUB_LOG" | tr -d ' ')"; }
probe inject_space   "sudo -n $HELPER apt '1.0 /tmp/evil.deb'"
probe inject_newline "sudo -n $HELPER apt \"\$(printf '1.0\n/tmp/evil.deb')\""
probe inject_path    "sudo -n $HELPER apt /tmp/evil.deb"
probe inject_option  "sudo -n $HELPER apt -o"
probe extra_arg      "sudo -n $HELPER apt 1.0 /tmp/evil.deb"
probe unknown_mgr    "sudo -n $HELPER rpm 1.0"
probe other_package  "sudo -n /usr/bin/apt-get install -y evil"
probe scope          "sudo -n /usr/bin/systemd-run --scope --collect --quiet --unit=squid-upgrade-1.9.9-1 --setenv=A='x y' bash /tmp/scoped.sh"
probe helper_tamper  "printf x >> $HELPER"

# ── write_status() from the real upgrade script, as the service user ───────
# The function body holds a heredoc whose JSON has a '}' at column 0, so end at
# the first column-0 '}' AFTER the heredoc terminator.
awk '/^write_status\(\) \{/{f=1} f{print} f&&/^STATUS_JSON$/{h=1} f&&h&&/^}$/{exit}' /seam/upgrade-linux-tentacle.sh > /tmp/write-status.sh
rm -f "$STATE/last-upgrade.json"
as_user "STATUS_DIR=$STATE; STATUS_FILE=$STATE/last-upgrade.json; SERVICE_USER=$USER_NAME; TARGET_VERSION=1.9.9; STARTED_AT=now; . /tmp/write-status.sh; write_status IN_PROGRESS seam" | sed 's/^/[status] /'
result status_written "$(grep -c '"status": "IN_PROGRESS"' "$STATE/last-upgrade.json" 2>/dev/null || echo 0)"
result status_owner "$(stat -c '%U' "$STATE/last-upgrade.json" 2>/dev/null)"

echo "SEAM_DONE"
