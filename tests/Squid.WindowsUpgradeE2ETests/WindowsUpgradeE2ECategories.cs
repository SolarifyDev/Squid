namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// xUnit Trait categories for the Windows upgrade E2E
/// suite. Mirrors the discipline in Squid.Tentacle.Tests.Support.TentacleTestCategories.
///
/// <para>The CI workflow filters by these categories so a developer running
/// the suite locally on macOS/Linux gets a clean "0 ran, 0 passed" rather
/// than spurious failures from `if (!OperatingSystem.IsWindows()) return`
/// short-circuits.</para>
/// </summary>
public static class WindowsUpgradeE2ECategories
{
    /// <summary>
    /// E2E coverage for the <c>WindowsTentacleUpgradeStrategy.BuildOuterWrapper</c>
    /// + Task Scheduler one-shot detach mechanism. Runs the wrapper PowerShell
    /// against a real Windows host, verifies <c>schtasks</c> registers + runs
    /// + auto-deletes the detached task, and that the inner script executes
    /// in a SYSTEM-identity process tree separate from the wrapper's own.
    /// </summary>
    public const string Wrapper = "WindowsUpgradeWrapperE2E";

    /// <summary>
    /// E2E coverage for the Phase B physical mechanics
    /// (Stop-Service / Move-Item swap / Start-Service / version verify)
    /// against a real <c>sc.exe</c>-installed Windows service. Uses
    /// <c>WindowsServiceFixture</c> to install + start + cleanup the
    /// minimal <c>SquidUpgradeE2ETestService</c> binary so the upgrade
    /// pipeline's filesystem-swap logic runs against a real running
    /// service, not a mock.
    /// </summary>
    public const string Service = "WindowsUpgradeServiceE2E";

    /// <summary>
    /// E2E coverage for the opportunistic SHA256
    /// companion-file fetch + verification logic in
    /// <c>upgrade-windows-tentacle.ps1</c>. Uses an in-process HTTP
    /// listener serving a known .sha256 + .zip pair so the .ps1's
    /// <c>Invoke-WebRequest</c> + <c>Get-FileHash</c> path runs against
    /// a real local server (no GitHub Releases dependency for the test
    /// — the fixture controls EVERY response: 404, invalid body, valid
    /// match, valid mismatch).
    /// </summary>
    public const string ShaVerify = "WindowsUpgradeShaVerifyE2E";
}
