using Squid.Core.Services.Machines.Upgrade.Methods;

namespace Squid.UnitTests.Services.Machines.Upgrade.Methods;

public sealed class AptUpgradeMethodTests
{
    private static readonly AptUpgradeMethod Method = new();

    [Fact]
    public void Name_IsLowercaseStableIdentifier()
    {
        Method.Name.ShouldBe("apt");
    }

    [Fact]
    public void RequiresExplicitSwap_IsFalse_BecauseDpkgWritesFilesDirectly()
    {
        // dpkg's transactional write IS the swap — Phase B's mv-swap block
        // would error trying to mv a non-existent $EXTRACT for apt installs.
        Method.RequiresExplicitSwap.ShouldBeFalse();
    }

    [Fact]
    public void Render_ProbesAptGetAndSquidSourcesFile()
    {
        // Both probes must be present: apt-get binary present AND OUR repo
        // configured. Without the sources.list check, `apt-get install
        // squid-tentacle` could find a colliding package from another repo.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("command -v apt-get");
        snippet.ShouldContain("/etc/apt/sources.list.d/squid.list");
    }

    [Fact]
    public void Render_PinsExactTargetVersion()
    {
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("squid-tentacle=1.4.0");
    }

    [Fact]
    public void Render_GatesOnInstallOk_ToShortCircuitWhenHigherPriorityMethodAlreadyRan()
    {
        // The contract every method honours — first line gates on $INSTALL_OK
        // so the script's apt → yum → tarball priority order works.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.TrimStart().ShouldStartWith("if [ \"$INSTALL_OK\" != \"1\" ]");
    }

    [Fact]
    public void Render_SetsBothInstallOkAndInstallMethod_OnSuccess()
    {
        // Phase B branches on INSTALL_METHOD; if a method sets INSTALL_OK
        // but forgets INSTALL_METHOD, Phase B can't decide whether to swap.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("INSTALL_OK=1");
        snippet.ShouldContain("INSTALL_METHOD=apt");
    }

    [Fact]
    public void Render_RecordsOldVersionForManualRollback()
    {
        // Status file consumes OLD_VERSION_APT to print a copy-paste-ready
        // downgrade command in the ROLLBACK_NEEDED detail.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("OLD_VERSION_APT=$(dpkg-query");
    }

    [Fact]
    public void Render_AllowsDowngrades_BecauseUpgradeRequestMaySpecifyOlderVersion()
    {
        // Operator-driven downgrades are a legitimate scenario (revert).
        // apt errors by default when downgrading; --allow-downgrades flag
        // is required to make `pkg=oldver` work.
        Method.RenderDetectAndInstall("1.4.0").ShouldContain("--allow-downgrades");
    }

    [Fact]
    public void Render_LogsSkipReason_WhenPrerequisitesAbsent()
    {
        // Operator running `journalctl -u squid-tentacle` or reading the
        // status file's detail field needs to see WHY apt was skipped, not
        // just observe that yum/tarball was tried instead.
        Method.RenderDetectAndInstall("1.4.0")
            .ShouldContain("[upgrade-method:apt] Skipped: apt-get not found OR /etc/apt/sources.list.d/squid.list missing");
    }

    [Fact]
    public void Render_DoesNotSetDebianFrontendEnvVar_BecauseSudoScrubsUnlistedEnv()
    {
        // Bug #4 from 1.4.3 E2E testing: passing `sudo DEBIAN_FRONTEND=noninteractive
        // apt-get install ...` gets rejected by sudo with:
        //   "sorry, you are not allowed to set the following environment
        //    variables: DEBIAN_FRONTEND"
        // sudo scrubs env by default and the sudoers rule has no SETENV: tag
        // or env_keep entry for DEBIAN_FRONTEND. Result: apt install never
        // runs and the upgrade fell through to tarball fallback, wasting
        // bandwidth + time for an operation the package manager would have
        // done in ~10s.
        //
        // Fix: rely on `apt-get install -y` alone to suppress prompts. Our
        // squid-tentacle .deb has no debconf questions, so the noninteractive
        // frontend var is pure belt-and-suspenders we can drop.
        //
        // Pin the absence of the RUNNABLE pattern `sudo DEBIAN_FRONTEND=` so
        // a refactor that restores the env-var-on-sudo syntax fails here. We
        // DON'T search for bare "DEBIAN_FRONTEND" because the comment above
        // the install line mentions it deliberately as a do-not-do warning.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldNotContain("sudo DEBIAN_FRONTEND=",
            customMessage: "sudo would reject `sudo DEBIAN_FRONTEND=... apt-get install` with " +
                           "'not allowed to set DEBIAN_FRONTEND' — apt install never runs, upgrade " +
                           "falls through to tarball. Rely on `-y` alone (squid-tentacle has no " +
                           "debconf questions).");
    }

    [Fact]
    public void Render_AptGetUpdate_IsScopedToSquidSourcesListOnly()
    {
        // Real production failure: a machine with an expired v2raya.org apt
        // GPG key broke our upgrade because `apt-get update` refreshes EVERY
        // source file and some apt versions exit non-zero when ANY source
        // fails. Fix: the targeted flags make `update` touch only squid.list.
        // If anyone drops these flags back to a bare `apt-get update`, this
        // test fails — the rendered form becomes fragile to user environment.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("-o Dir::Etc::sourcelist=sources.list.d/squid.list",
            customMessage: "apt-get update must be scoped to squid.list — unrelated broken repos must not break our upgrade");
        snippet.ShouldContain("-o Dir::Etc::sourceparts=-",
            customMessage: "sourceparts=- disables sources.list.d/* fanout — required alongside sourcelist for the scoping to work");
        snippet.ShouldContain("-o APT::Get::List-Cleanup=0",
            customMessage: "List-Cleanup=0 prevents apt from pruning other source list caches when we did a scoped refresh");
    }

    [Fact]
    public void Render_DpkgLockPreemption_WaitsForKnownAutoUpdaters()
    {
        // A3 (1.6.0): if /var/lib/dpkg/lock-frontend is held by apt-daily
        // or unattended-upgrades, wait up to 90s for them to release
        // before our apt install (which would otherwise lose the race).
        // Pin: the lock probe, the known-process allowlist, the bounded
        // wait, and the no-kill stance.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("sudo fuser /var/lib/dpkg/lock-frontend",
            customMessage: "must use fuser (not lsof — not always installed) to detect lock holder");

        snippet.ShouldContain("unattended-upgr*|apt|apt-get|aptd|update-manager|packagekitd",
            customMessage: "lock-wait must be gated on known auto-updater process names — interactive admin sessions get a different (warn-then-proceed) path");

        snippet.ShouldContain("for wait_iter in $(seq 1 90)",
            customMessage: "wait must be bounded (90s = comfortably > apt-daily's typical 30-60s runtime)");

        snippet.ShouldNotContain("kill -",
            customMessage: "must NEVER SIGTERM unattended-upgrades — interrupting mid-transaction leaves dpkg needs-configure, harder to recover from than waiting");
    }

    [Fact]
    public void Render_DpkgLockPreemption_UnknownHolder_WarnsAndProceeds()
    {
        // The default branch of the case statement: an unknown lock holder
        // (could be an interactive admin) gets a warning + proceed. Don't
        // wait 90s for what might be a `sudo apt edit-sources` session.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("dpkg lock held by unknown process",
            customMessage: "unknown holder must be logged with the actual process name so operators can diagnose");
        snippet.ShouldContain("proceeding without wait",
            customMessage: "unknown holder must NOT trigger the 90s wait — apt's own contention error gives operator a faster signal");
    }

    [Fact]
    public void Render_DownloadsPreUpgradeSnapshot_ToFixedRollbackPath()
    {
        // C1 (1.6.0): apt method downloads the CURRENT version's .deb from
        // GitHub Releases BEFORE triggering apt install, so Phase B can
        // dpkg -i it on healthz failure. Pin the snapshot URL pattern,
        // download tool flags, and destination path so refactors can't
        // silently break auto-rollback.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("SNAPSHOT_URL=\"${GH_BASE_URL}/${OLD_VERSION_APT}/squid-tentacle_${OLD_VERSION_APT}_${ARCH_DEB}.deb\"",
            customMessage: "snapshot URL must follow GitHub Release artefact naming so the same .deb operators can copy-paste matches what we auto-fetch");
        snippet.ShouldContain("SNAPSHOT_PATH=\"/var/lib/squid-tentacle/rollback/squid-tentacle_${OLD_VERSION_APT}_${ARCH_DEB}.deb\"",
            customMessage: "snapshot destination must be /var/lib/squid-tentacle/rollback/ — sudoers rule pins this prefix");
        snippet.ShouldContain("curl -fsSL --connect-timeout 15 --max-time 120 --retry 2",
            customMessage: "snapshot download must have bounded timeout (120s) and retries — long hang here delays the upgrade");
        snippet.ShouldContain("SQUID_UPGRADE_ROLLBACK_SNAPSHOT=\"$SNAPSHOT_PATH\"",
            customMessage: "successful download must export the path via env var so Phase B can find the snapshot");
    }

    [Fact]
    public void Render_SnapshotDownloadFailure_IsNonFatal_UpgradeStillProceeds()
    {
        // C1 (1.6.0): if snapshot download fails (network blip, GH outage,
        // OLD_VERSION not in releases), the upgrade itself MUST still
        // proceed — auto-rollback is a "best effort" feature, not a
        // prerequisite. Operator falls back to manual rollback instruction
        // in the ROLLBACK_NEEDED detail.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("snapshot download failed — auto-rollback unavailable",
            customMessage: "must explicitly log the snapshot-download failure so operators understand auto-rollback is unavailable for THIS upgrade");
        // The non-fatal property: the snapshot block doesn't `exit` on download failure.
        // Pin by checking that the apt-get install line appears AFTER the snapshot block —
        // both run regardless of snapshot success.
        var snapshotIdx = snippet.IndexOf("SNAPSHOT_URL=", StringComparison.Ordinal);
        var aptInstallIdx = snippet.IndexOf("apt-get install -y --allow-downgrades", StringComparison.Ordinal);
        aptInstallIdx.ShouldBeGreaterThan(snapshotIdx,
            customMessage: "apt install must come AFTER snapshot block — confirms snapshot is best-effort, not blocking");
    }

    [Fact]
    public void Render_SnapshotSkipped_WhenNoOldVersionInstalled()
    {
        // First-time install (OLD_VERSION_APT == "<none>") has nothing
        // to roll back TO — skipping the snapshot avoids a guaranteed-fail
        // GitHub fetch for a version string that's not a valid release.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("if [ \"$OLD_VERSION_APT\" != \"<none>\" ]",
            customMessage: "snapshot download must be guarded by 'previous version exists' — first-time installs have no rollback target");
    }

    [Fact]
    public void Render_SnapshotDownload_ValidatesOldVersionAgainstSemverWhitelist()
    {
        // Audit D.2 fix (1.6.x): OLD_VERSION_APT comes from dpkg-query at
        // runtime — normally clean semver, but a corrupted package DB
        // could surface bytes with shell/URL metachars. Before the fix,
        // the value was embedded directly into the GitHub URL, which is
        // an injection vector if dpkg output is untrusted.
        //
        // Pin: strict whitelist `^[a-zA-Z0-9._~+-]+$` guards the URL
        // construction — mirrors the safe_version() semantics in the
        // main script's Phase B rollback path.
        var snippet = Method.RenderDetectAndInstall("1.6.0");

        snippet.ShouldContain("grep -qE '^[a-zA-Z0-9._~+-]+$'",
            customMessage: "OLD_VERSION_APT must be validated against semver-ish whitelist BEFORE URL construction — prevents shell/URL injection from corrupted dpkg-query output");
        snippet.ShouldContain("contains unexpected characters — skipping snapshot download",
            customMessage: "rejected-value path must log a clear message so operator sees why auto-rollback is unavailable on this upgrade");
    }

    [Fact]
    public void Render_AptOperations_CapNetworkTimeoutAndRetries()
    {
        // Without explicit timeouts, `apt-get install` on a wedged proxy
        // (v2raya, clash, enterprise MITM) stalled for 12+ minutes in prod —
        // enough to blow through Halibut's script observation window and
        // leave the upgrade stuck in IN_PROGRESS with no operator signal.
        // The cap makes failures fail fast so the tarball fallback runs.
        var snippet = Method.RenderDetectAndInstall("1.4.0");

        snippet.ShouldContain("-o Acquire::http::Timeout=60",
            customMessage: "apt-get update must cap per-connection timeout to 60s so a stalled proxy doesn't eat the whole upgrade budget");
        snippet.ShouldContain("-o Acquire::http::Timeout=120",
            customMessage: "apt-get install must cap per-connection timeout to 120s (.deb downloads are bigger than index files)");
        snippet.ShouldContain("-o Acquire::Retries=1",
            customMessage: "apt's default 3-retry policy + long timeouts = 10min+ hangs; single retry is sufficient for our flow");
    }
}
