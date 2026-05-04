using System.Reflection;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// P1-Phase12.E.7.A-1.c — minimal smoke test for
/// <see cref="WindowsServiceFixture"/>. Establishes that the fixture's
/// install/start/marker-write/stop/uninstall lifecycle ACTUALLY works
/// end-to-end on a real Windows host BEFORE the A-2 upgrade tests
/// depend on it. If A-2 ever fails, the smoke test pinpoints whether
/// the fixture is broken vs the upgrade flow is broken.
///
/// <para>Each test no-ops via <see cref="WindowsServiceFixture.IsAvailable"/>
/// on macOS/Linux dev boxes (mirrors the existing
/// <c>WindowsUpgradeWrapperE2ETests</c> pattern). Real assertions fire
/// only on the <c>windows-latest</c> GHA runner where sc.exe + admin
/// rights exist.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.Service)]
public sealed class WindowsServiceFixtureSmokeE2ETests
{
    [Fact]
    public void Fixture_FullLifecycle_InstallStartMarkerStopUninstall()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        var serviceName = $"SquidUpgradeFixtureSmoke_{Guid.NewGuid():N}";
        var installDir = Path.Combine(Path.GetTempPath(), $"squid-upgrade-fixture-smoke-{Guid.NewGuid():N}");
        var testServiceExe = LocateTestServiceExe();

        using var fixture = new WindowsServiceFixture(serviceName, installDir);

        // Install + Start. Polls for STATE: RUNNING up to 30s.
        fixture.InstallAndStart(testServiceExe, initialVersion: "smoke-1.0.0", startTimeout: TimeSpan.FromSeconds(30));

        // The service writes a marker on Start containing the version it
        // read from version.txt. Polling a few seconds because OnStart is
        // called on a worker thread and File.WriteAllText is async-ish to
        // the SCM acceptance.
        var markerSeen = WaitForFileContent(fixture.MarkerFilePath, "smoke-1.0.0", TimeSpan.FromSeconds(15));
        markerSeen.ShouldBeTrue(
            customMessage: $"marker file at {fixture.MarkerFilePath} did not contain 'smoke-1.0.0' within 15s — proves the service started AND its OnStart handler executed AND it read version.txt correctly. If this fails: check sc query manually, then check service log");

        // Stop. Polls for STATE: STOPPED up to 10s. Marker should be gone
        // (the service deletes it in OnStop).
        fixture.Stop(TimeSpan.FromSeconds(10));

        var markerGone = WaitForFileAbsent(fixture.MarkerFilePath, TimeSpan.FromSeconds(10));
        markerGone.ShouldBeTrue(
            customMessage: "service's OnStop handler should have deleted the marker — if not, OnStop didn't run cleanly");

        // Uninstall. The service is gone from sc database. Verify by
        // attempting to query — should return non-zero.
        fixture.Uninstall();
    }

    [Fact]
    public void Fixture_RepeatedInstall_DoesNotFailDueToStaleService()
    {
        // Idempotency check: a previous test (or environment leftover)
        // installed the same-named service. Fixture must clean up first
        // and proceed without error.
        if (!WindowsServiceFixture.IsAvailable) return;

        var serviceName = $"SquidUpgradeFixtureRepeat_{Guid.NewGuid():N}";
        var installDir = Path.Combine(Path.GetTempPath(), $"squid-upgrade-fixture-repeat-{Guid.NewGuid():N}");
        var testServiceExe = LocateTestServiceExe();

        // First install — must succeed.
        using (var fixture = new WindowsServiceFixture(serviceName, installDir))
        {
            fixture.InstallAndStart(testServiceExe, "v1", TimeSpan.FromSeconds(30));
            fixture.Stop(TimeSpan.FromSeconds(10));
            // INTENTIONAL: don't Uninstall. Simulate a previous test crashing mid-flight.
        }

        // Second install with the SAME service name — must succeed (fixture
        // pre-deletes any stale install). Without idempotency, this would
        // fail with "service already exists".
        using (var fixture = new WindowsServiceFixture(serviceName, installDir))
        {
            fixture.InstallAndStart(testServiceExe, "v2", TimeSpan.FromSeconds(30));
            var markerSeen = WaitForFileContent(fixture.MarkerFilePath, "v2", TimeSpan.FromSeconds(15));
            markerSeen.ShouldBeTrue("second install must succeed AND the new version must take effect");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Locates the test-service exe. Project-reference + the assembly's
    /// own location both point at the test build output dir; the test
    /// service exe lands in a sibling dir under bin/Debug or bin/Release.
    /// </summary>
    private static string LocateTestServiceExe()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        // From `tests/Squid.WindowsUpgradeE2ETests/bin/<Config>/net9.0/`
        // the test service builds to
        // `tests/Squid.WindowsUpgradeE2E.TestService/bin/<Config>/net9.0/SquidUpgradeE2ETestService.exe`.
        // Walk up to the bin parent then sideways.
        var configDir = Path.GetDirectoryName(thisAssemblyDir)!;     // bin/<Config>
        var binDir = Path.GetDirectoryName(configDir)!;              // bin
        var testProjectDir = Path.GetDirectoryName(binDir)!;         // tests/Squid.WindowsUpgradeE2ETests
        var testsDir = Path.GetDirectoryName(testProjectDir)!;       // tests

        var configName = Path.GetFileName(configDir);
        var tfmName = Path.GetFileName(thisAssemblyDir);

        var candidate = Path.Combine(testsDir, "Squid.WindowsUpgradeE2E.TestService", "bin", configName, tfmName, "SquidUpgradeE2ETestService.exe");

        if (!File.Exists(candidate))
            throw new FileNotFoundException(
                $"test-service exe not found at expected location: {candidate}. " +
                $"Verify the project reference Squid.WindowsUpgradeE2ETests → Squid.WindowsUpgradeE2E.TestService " +
                $"is wired and the test-service has been built (`dotnet build` should cascade).");

        return candidate;
    }

    private static bool WaitForFileContent(string path, string expectedContent, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var actual = File.ReadAllText(path).Trim();
                    if (actual == expectedContent) return true;
                }
            }
            catch { /* file might be mid-write; retry */ }

            Thread.Sleep(200);
        }
        return false;
    }

    private static bool WaitForFileAbsent(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path)) return true;
            Thread.Sleep(200);
        }
        return false;
    }
}
