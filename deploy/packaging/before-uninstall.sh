#!/bin/sh
# ==============================================================================
# Squid Tentacle — pre-uninstall hook
# ------------------------------------------------------------------------------
# Invoked by dpkg / rpm BEFORE the package contents are removed from
# /opt/squid-tentacle/. At this point the systemd service (if installed) may
# still be running. We deliberately do NOT stop or deregister the service
# here — that's destructive and would also fire on package upgrades (dpkg
# calls the OLD package's before-uninstall during upgrades to the NEW one),
# which would briefly leave operators without a running agent for every
# `apt upgrade` — unacceptable.
#
# Matches Octopus's linux-packages/content/before-uninstall.sh (intentionally
# empty). Operator removal flow documented in uninstall-tentacle.md:
#   1. sudo squid-tentacle service --stop --uninstall
#   2. sudo squid-tentacle delete-instance
#   3. sudo apt-get remove squid-tentacle   (or `yum remove`)
#
# Doing it in that order avoids orphan systemd units pointing at a
# nonexistent binary.
# ==============================================================================

# No-op — documented reasoning above; leaving the hook defined so dpkg/rpm
# have a valid script slot (some package tools complain about missing ones).
exit 0
