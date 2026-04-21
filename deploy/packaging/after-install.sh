#!/bin/sh
# ==============================================================================
# Squid Tentacle — post-install / post-upgrade hook
# ------------------------------------------------------------------------------
# Invoked by dpkg / rpm after the package contents have been copied to
# /opt/squid-tentacle/ and the /usr/bin/squid-tentacle symlink is in place.
#
# DELIBERATELY NO `systemctl restart` HERE — matches Octopus's approach in
# their linux-packages/content/after-install.sh. Reason: the running
# squid-tentacle service holds its binary file open in memory and keeps
# running the OLD code even after dpkg swaps the binary on disk. The
# server-side in-UI upgrade flow (LinuxTentacleUpgradeStrategy +
# upgrade-linux-tentacle.sh) is what triggers the restart *inside a
# detached systemd scope* so the upgrade script itself doesn't get killed
# by the restart. Attempting a restart here would break that contract and
# reintroduce the Phase 1 self-kill bug.
#
# What gets left to the operator:
#   1. Either run the in-UI "Upgrade" button (works for existing registered
#      tentacles — server sends the upgrade script which handles restart),
#   2. Or restart manually: `sudo systemctl restart squid-tentacle`,
#   3. Or for first-time installs: follow the registration + service-install
#      steps printed below.
# ==============================================================================

# Detect whether we're a first-time install or an upgrade. dpkg passes
# "configure" + old version on upgrade; "configure" + empty on fresh install.
# Silently no-op during removal / failed-upgrade phases.
case "${1:-}" in
  configure|2|'')
    ;;
  *)
    exit 0
    ;;
esac

BIN="/opt/squid-tentacle/Squid.Tentacle"

if [ ! -x "$BIN" ]; then
    echo ""
    echo "Warning: Squid Tentacle installed but $BIN is missing or not executable."
    echo "This usually indicates a corrupted package — reinstall or verify the release."
    exit 0
fi

# Version-aware message: upgrades just acknowledge; fresh installs print the
# full getting-started block so operators aren't hunting docs after a first install.
if getent passwd squid-tentacle >/dev/null 2>&1 \
    && [ -f /etc/squid-tentacle/instances/Default.config.json ]; then
    # Upgrade path — an instance already exists.
    # Get the staged version. THREE layers of defence:
    #   1. `version` subcommand (not `--version`): pre-1.4.2 tentacles routed
    #      `--version` to RunCommand, which STARTS THE AGENT. The postinst's
    #      subshell then hangs forever (agent never exits, head -1 closes the
    #      pipe but the agent ignores SIGPIPE in dotnet's I/O stack). Bug
    #      observed in 1.4.1/1.4.2 prod downgrade testing.
    #   2. `</dev/null`: don't share the postinst's stdin with the binary —
    #      if the binary tries to read stdin for any reason, EOF returns
    #      immediately instead of blocking.
    #   3. `timeout 5`: hard cap. Even if the binary somehow hangs, dpkg's
    #      postinst never spends more than 5s on this cosmetic version print.
    VERSION_STAGED=$(timeout 5 "$BIN" version </dev/null 2>/dev/null | head -1 || echo 'unknown version')
    echo ""
    echo "Squid Tentacle upgraded to ${VERSION_STAGED}."
    echo "Binary is staged on disk. To switch to the new version:"
    echo "    sudo systemctl restart squid-tentacle"
    echo ""
    echo "(The in-UI 'Upgrade' button in the Squid web portal does this for you automatically.)"
    echo ""
else
    # Fresh install — show the full onboarding message.
    echo ""
    echo "=== Squid Tentacle installed ==="
    echo ""
    echo "Next steps:"
    echo ""
    echo "1. Register this host with your Squid server:"
    echo ""
    echo "   sudo squid-tentacle register \\"
    echo "     --server https://your-squid-server:7078 \\"
    echo "     --api-key API-XXXX \\"
    echo "     --role web-server \\"
    echo "     --environment Production \\"
    echo "     --flavor LinuxTentacle"
    echo ""
    echo "2. Install the systemd service:"
    echo ""
    echo "   sudo squid-tentacle service install"
    echo "   sudo systemctl enable --now squid-tentacle"
    echo ""
    echo "Documentation: https://github.com/SolarifyDev/Squid/blob/main/README.md"
    echo ""
fi

exit 0
