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
/// <para><b>Why no rollback in v1:</b> apt downgrade requires the OLD
/// version to still be in the repo cache (usually true) AND
/// <c>--allow-downgrades</c> flag. Phase 2 Part 2 v1 does NOT auto-roll-back
/// from apt-installed failures — operator does <c>apt-get install
/// --allow-downgrades squid-tentacle=$OLD</c>. Phase 2 Part 2 v2 may add
/// auto-rollback if user demand justifies the complexity.</para>
/// </remarks>
public sealed class AptUpgradeMethod : ILinuxUpgradeMethod
{
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
                     # Capture the currently-installed version BEFORE upgrade so the operator can roll back manually if needed.
                     OLD_VERSION_APT=$(dpkg-query -W -f='${Version}' squid-tentacle 2>/dev/null || echo "<none>")
                     echo "[upgrade-method:apt] Pre-upgrade version: $OLD_VERSION_APT"
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
