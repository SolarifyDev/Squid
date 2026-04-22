namespace Squid.Core.Services.Machines.Upgrade.Methods;

/// <summary>
/// Install via the Debian/Ubuntu APT repo at
/// <c>https://squid.solarifyai.com/apt</c>. Activated when the agent host
/// has <c>apt-get</c> AND the Squid sources file at
/// <c>/etc/apt/sources.list.d/squid.list</c>. Both are configured by
/// <c>install-tentacle.sh</c> on fresh installs (Phase 2 Part 2 change).
/// </summary>
/// <remarks>
/// <para><b>Version pinning:</b> <c>apt-get install pkg=version</c>
/// installs that specific version. If the version isn't in the repo, apt
/// errors and we fall through to the next method (yum or tarball).</para>
///
/// <para><b>No swap needed:</b> dpkg writes /opt/squid-tentacle/ atomically
/// (within its own transaction model) — the post-scope phase only needs to
/// restart the service. The running tentacle still holds the OLD binary in
/// memory until restart, which matches the assumption Phase 1 made for the
/// scope-detach mechanism.</para>
///
/// <para><b>Auto-rollback (C1+C2, 1.6.0):</b> before triggering apt
/// install, the script downloads the CURRENT version's <c>.deb</c> from
/// GitHub Releases (best-effort, ~60MB) into
/// <c>/var/lib/squid-tentacle/rollback/</c>. The path is propagated to
/// Phase B via <c>SQUID_UPGRADE_ROLLBACK_SNAPSHOT</c>. If post-restart
/// healthz fails, Phase B runs <c>dpkg -i --force-downgrade snapshot.deb</c>
/// + <c>systemctl restart</c> to restore the previous version
/// automatically. Snapshot download failure is non-fatal — upgrade still
/// proceeds, but auto-rollback is unavailable for that attempt and the
/// operator falls back to the manual instruction in the status detail.</para>
/// </remarks>
public sealed class AptUpgradeMethod : ILinuxUpgradeMethod
{
    /// <summary>
    /// Directory where the pre-upgrade rollback snapshot .deb is stored.
    /// Owned by the service user; pre-created by install-tentacle.sh as
    /// part of the state dir provisioning. Mirror constant on the Phase B
    /// failure-rollback path — both must reference the same string.
    /// </summary>
    public const string RollbackSnapshotDir = "/var/lib/squid-tentacle/rollback";

    /// <summary>
    /// Base URL for GitHub Releases artefacts. Same env-var name pattern
    /// as <see cref="LinuxTentacleUpgradeStrategy.DownloadBaseUrlEnvVar"/>
    /// so air-gapped operators with a private mirror can override both.
    /// </summary>
    public const string GitHubReleaseBaseUrlEnvVar = "SQUID_GITHUB_RELEASE_BASE_URL";

    /// <summary>
    /// Default GitHub URL template for the .deb. Two interpolations:
    /// <c>{0}</c> = version, <c>{1}</c> = arch (amd64/arm64). Operators can
    /// override via the env var above for private-mirror scenarios.
    /// </summary>
    private const string DefaultGitHubReleaseBaseUrl = "https://github.com/SolarifyDev/Squid/releases/download";

    public string Name => "apt";

    public bool RequiresExplicitSwap => false;

    public string RenderDetectAndInstall(string targetVersion)
    {
        // Bash snippet — emitted verbatim into the upgrade script template.
        // Detection: apt-get on PATH AND the Squid sources file present.
        // The sources file existence is what guarantees `apt-get install
        // squid-tentacle` will look in OUR repo, not just any random apt
        // repo that happens to have a `squid-tentacle` package by collision.
        //
        // Targeted `apt-get update`: we MUST NOT refresh every repo the
        // operator happens to have. A stale/broken third-party repo (e.g.
        // v2raya.org with an expired GPG key) would make `apt-get update`
        // emit loud errors and — on some apt versions — exit non-zero,
        // breaking our upgrade for a problem we didn't cause. Flags used:
        //   -o Dir::Etc::sourcelist=sources.list.d/squid.list
        //   -o Dir::Etc::sourceparts=-           (disable all source.list.d/*)
        //   -o APT::Get::List-Cleanup=0          (don't prune other lists)
        //   -o Acquire::http::Timeout=60         (fail fast on wedged networks)
        //   -o Acquire::Retries=1                (single retry, not default 3)
        // Sudoers has a matching NOPASSWD rule for this exact invocation.
        return $$"""
                 if [ "$INSTALL_OK" != "1" ]; then
                   if command -v apt-get >/dev/null 2>&1 && [ -f /etc/apt/sources.list.d/squid.list ]; then
                     echo "[upgrade-method:apt] Squid APT repo configured — attempting `apt-get install squid-tentacle={{targetVersion}}`"

                     # ── A3 (1.6.0): dpkg lock preemption ────────────────────────────
                     # Wait up to 90s if /var/lib/dpkg/lock-frontend is held by a
                     # known background updater (apt-daily, unattended-upgrades).
                     # Without this, our apt install would race the timer-driven
                     # OS updater and lose with a generic "could not get lock"
                     # error, falling through to tarball unnecessarily.
                     #
                     # Conservative: only wait when the holder is one of the known
                     # Debian/Ubuntu auto-updaters (process name match). An
                     # unknown holder might be an interactive admin session — we
                     # don't want to wait 90s for that. Log + proceed; apt's own
                     # lock contention will give a clear error.
                     #
                     # No `kill` — never SIGTERM a running unattended-upgrades.
                     # Their transactions are atomic and interrupting mid-flight
                     # leaves dpkg in needs-configure state that's harder to
                     # recover from than waiting 60s.
                     if command -v fuser >/dev/null 2>&1; then
                       LOCK_HOLDER_PID=$(sudo fuser /var/lib/dpkg/lock-frontend 2>/dev/null | tr -d ' ' || true)
                       if [ -n "$LOCK_HOLDER_PID" ]; then
                         LOCK_HOLDER_CMD=$(ps -p "$LOCK_HOLDER_PID" -o comm= 2>/dev/null | tr -d ' ' || echo "unknown")
                         case "$LOCK_HOLDER_CMD" in
                           unattended-upgr*|apt|apt-get|aptd|update-manager|packagekitd)
                             echo "[upgrade-method:apt] dpkg lock held by $LOCK_HOLDER_CMD (PID $LOCK_HOLDER_PID) — waiting up to 90s for it to release"
                             for wait_iter in $(seq 1 90); do
                               sleep 1
                               sudo fuser /var/lib/dpkg/lock-frontend 2>/dev/null >/dev/null || break
                             done
                             if sudo fuser /var/lib/dpkg/lock-frontend 2>/dev/null >/dev/null; then
                               echo "[upgrade-method:apt] WARNING: dpkg lock STILL held after 90s; apt install may fail"
                             else
                               echo "[upgrade-method:apt] dpkg lock released"
                             fi
                             ;;
                           *)
                             echo "[upgrade-method:apt] WARNING: dpkg lock held by unknown process '$LOCK_HOLDER_CMD' (PID $LOCK_HOLDER_PID) — proceeding without wait, apt install may fail"
                             ;;
                         esac
                       fi
                     fi

                     # Capture the currently-installed version BEFORE upgrade so the operator can roll back manually if needed.
                     OLD_VERSION_APT=$(dpkg-query -W -f='${Version}' squid-tentacle 2>/dev/null || echo "<none>")
                     echo "[upgrade-method:apt] Pre-upgrade version: $OLD_VERSION_APT"

                     # ── C1 (1.6.0): pre-upgrade snapshot for auto-rollback ─────────
                     # Download the CURRENT version's .deb from GitHub Releases so
                     # Phase B can `dpkg -i --force-downgrade` it on health-check
                     # failure. Best-effort — if download fails (network blip, GH
                     # outage, OLD_VERSION_APT not in releases for some reason),
                     # we lose auto-rollback for THIS upgrade attempt but the
                     # upgrade itself proceeds and operator falls back to the
                     # manual rollback instruction in the ROLLBACK_NEEDED detail.
                     #
                     # Input validation (audit D.2 / 1.6.x fix): OLD_VERSION_APT
                     # comes from dpkg-query at runtime. Normally plain semver,
                     # but a corrupted package DB could theoretically surface
                     # bytes with shell / URL metacharacters. Validate against a
                     # strict semver-ish whitelist BEFORE embedding in the
                     # snapshot URL or file path — mirrors safe_version() in the
                     # main script's Phase B rollback path.
                     SQUID_UPGRADE_ROLLBACK_SNAPSHOT=""
                     if [ "$OLD_VERSION_APT" != "<none>" ] \
                          && printf '%s' "$OLD_VERSION_APT" | grep -qE '^[a-zA-Z0-9._~+-]+$'; then
                       ARCH_DEB=$(dpkg --print-architecture 2>/dev/null || echo amd64)
                       GH_BASE_URL="${SQUID_GITHUB_RELEASE_BASE_URL:-https://github.com/SolarifyDev/Squid/releases/download}"
                       SNAPSHOT_URL="${GH_BASE_URL}/${OLD_VERSION_APT}/squid-tentacle_${OLD_VERSION_APT}_${ARCH_DEB}.deb"
                       SNAPSHOT_PATH="/var/lib/squid-tentacle/rollback/squid-tentacle_${OLD_VERSION_APT}_${ARCH_DEB}.deb"
                       sudo mkdir -p /var/lib/squid-tentacle/rollback 2>/dev/null || true
                       echo "[upgrade-method:apt] Downloading rollback snapshot from $SNAPSHOT_URL"
                       if curl -fsSL --connect-timeout 15 --max-time 120 --retry 2 --retry-delay 5 \
                            "$SNAPSHOT_URL" -o "${SNAPSHOT_PATH}.tmp" 2>&1 | tail -3; then
                         sudo mv "${SNAPSHOT_PATH}.tmp" "$SNAPSHOT_PATH" 2>/dev/null
                         SQUID_UPGRADE_ROLLBACK_SNAPSHOT="$SNAPSHOT_PATH"
                         echo "[upgrade-method:apt] Rollback snapshot saved to $SNAPSHOT_PATH"
                       else
                         echo "[upgrade-method:apt] WARNING: snapshot download failed — auto-rollback unavailable for this upgrade (will fall back to manual instruction on failure)"
                         rm -f "${SNAPSHOT_PATH}.tmp" 2>/dev/null
                       fi
                     elif [ "$OLD_VERSION_APT" != "<none>" ]; then
                       echo "[upgrade-method:apt] WARNING: OLD_VERSION_APT='$OLD_VERSION_APT' contains unexpected characters — skipping snapshot download to avoid URL injection"
                     fi

                     # NOTE: NO `DEBIAN_FRONTEND=noninteractive` env var on the install line.
                     # sudo scrubs env by default and only permits explicitly-listed vars via
                     # env_keep / SETENV: tag. Bash syntax `sudo ENV=VAL cmd` tries to pass
                     # ENV through sudo; without permission, sudo rejects the WHOLE command
                     # with "sorry, you are not allowed to set the following environment
                     # variables: DEBIAN_FRONTEND" — apt install never runs, falls through
                     # to yum/tarball. Discovered in 1.4.3 E2E testing. The `-y` flag
                     # alone is enough to suppress interactive prompts for our squid-tentacle
                     # package (it has no debconf questions); DEBIAN_FRONTEND=noninteractive
                     # was belt-and-suspenders we can safely drop.
                     if sudo apt-get update -qq \
                            -o Dir::Etc::sourcelist=sources.list.d/squid.list \
                            -o Dir::Etc::sourceparts=- \
                            -o APT::Get::List-Cleanup=0 \
                            -o Acquire::http::Timeout=60 \
                            -o Acquire::Retries=1 \
                         && sudo apt-get install -y --allow-downgrades \
                                -o Acquire::http::Timeout=120 \
                                -o Acquire::Retries=1 \
                                "squid-tentacle={{targetVersion}}"; then
                       INSTALL_OK=1
                       INSTALL_METHOD=apt
                       echo "[upgrade-method:apt] Installed squid-tentacle={{targetVersion}} via apt"
                     else
                       echo "[upgrade-method:apt] apt-get install failed (exit $?); falling through to next method"
                     fi
                   else
                     echo "[upgrade-method:apt] Skipped: apt-get not found OR /etc/apt/sources.list.d/squid.list missing"
                   fi
                 fi
                 """;
    }
}
