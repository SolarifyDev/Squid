namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// xUnit Trait categories for the Linux upgrade E2E suite. Mirrors
/// <c>WindowsUpgradeE2ECategories</c> for the Windows project.
///
/// <para>The CI workflow filters by these categories. A developer running
/// the suite locally on macOS/Windows gets a clean "0 ran, 0 passed"
/// rather than spurious failures from
/// <c>if (!OperatingSystem.IsLinux()) return</c> short-circuits.</para>
/// </summary>
public static class LinuxTentacleE2ECategories
{
    /// <summary>
    /// Phase 12.L.E.1 baseline E2E coverage for the production
    /// <c>upgrade-linux-tentacle.sh</c> placeholder substitution + bash
    /// parse-cleanliness contract. Cross-platform safe (uses bash -n
    /// available on Linux + macOS); doesn't actually run an upgrade.
    ///
    /// <para>Subsequent phases (12.L.E.2+) extend this with real
    /// systemd-run / systemctl restart / apt / dnf / curl flows.</para>
    /// </summary>
    public const string UpgradeScript = "LinuxTentacleUpgradeScriptE2E";

    /// <summary>
    /// Phase 12.L.E.4+ E2E coverage for the production
    /// <c>upgrade-linux-tentacle.sh</c> end-to-end against a real
    /// <see cref="Infrastructure.LocalReleaseMirror"/> + (later)
    /// <see cref="Infrastructure.LinuxServiceFixture"/>. Tier 🟢
    /// high-fidelity. Linux-only — requires bash + sudo. CI runs on
    /// <c>ubuntu-latest</c>.
    /// </summary>
    public const string UpgradeLifecycle = "LinuxTentacleUpgradeLifecycleE2E";

    /// <summary>
    /// Phase 12.L.E.3 smoke coverage for <see cref="Infrastructure.LinuxServiceFixture"/>.
    /// Tier 🔵 Fixture-only (Rule 12) — does NOT count toward production
    /// E2E coverage. Subsequent phases (12.L.E.4+) consume the fixture
    /// for real upgrade-lifecycle tests where the fixture's systemd
    /// service is the target of <c>systemctl restart</c> swap mechanics.
    ///
    /// <para>Linux-only: requires systemd + passwordless sudo. Skip-
    /// guards on macOS/Windows + on Linux dev hosts without sudo
    /// configured. GHA <c>ubuntu-latest</c> runner has both.</para>
    /// </summary>
    public const string ServiceFixture = "LinuxServiceFixtureE2E";

    /// <summary>
    /// Phase 12.M.L.A.1+ E2E coverage for the production
    /// <c>deploy/scripts/install-tentacle.sh</c> bootstrap installer.
    /// Tier 🟢 high-fidelity — drives the real <c>.sh</c> against a real
    /// <see cref="Infrastructure.LocalReleaseMirror"/> + real bash + real
    /// curl + real sudo. Linux-only; skip-guards on macOS/Windows.
    ///
    /// <para>Pollution surface managed via env-var overrides:
    /// <c>INSTALL_DIR</c>=test-private temp dir, <c>NO_PKG_INSTALL=1</c>
    /// (skips APT/RPM repo writes), <c>CREATE_USER=no</c> (skips useradd
    /// for the system user). Tests that DO need the full pollution
    /// matrix (happy-path install with systemd unit + sudoers) handle
    /// cleanup via the fixture's IDisposable per Rule 12.3.</para>
    /// </summary>
    public const string InstallScript = "LinuxTentacleInstallScriptE2E";
}
