using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.L.E.3 — smoke tests for <see cref="LinuxServiceFixture"/>
/// itself. Tier 🔵 Fixture-only (Rule 12) — proves the test harness
/// works against real systemd before later phases consume it for
/// production-flow E2E.
///
/// <para>Mirrors <c>WindowsServiceFixtureSmokeE2ETests</c> in the sibling
/// project: install / start / verify marker / stop / verify marker gone /
/// uninstall, plus an idempotent re-install test.</para>
///
/// <para><b>Skip-on-non-Linux + skip-on-no-sudo</b>: the fixture's
/// <see cref="LinuxServiceFixture.IsAvailable"/> probe handles both
/// dimensions (OS check + passwordless sudo check). macOS/Windows dev
/// hosts return false; Linux hosts without sudo configured also return
/// false. CI runs on <c>ubuntu-latest</c> which has both.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.ServiceFixture)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class LinuxServiceFixtureSmokeE2ETests
{
    [Fact]
    public void Fixture_FullLifecycle_InstallStartMarkerStopUninstall()
    {
        if (!LinuxServiceFixture.IsAvailable) return;

        var serviceName = $"squid-linux-fixture-smoke-{Guid.NewGuid():N}";
        var installDir = Path.Combine(Path.GetTempPath(), $"squid-linux-fixture-smoke-{Guid.NewGuid():N}");
        var testServiceScript = LocateTestServiceScript();

        using var fixture = new LinuxServiceFixture(serviceName, installDir);

        // 1. Install + start at v1. Marker should appear with v1 within
        //    the timeout window.
        fixture.InstallAndStart(testServiceScript, initialVersion: "1.0.0", startTimeout: TimeSpan.FromSeconds(15));

        // 2. Service is active per systemctl.
        fixture.IsActive().ShouldBeTrue(
            customMessage: $"systemctl is-active should return 'active' after InstallAndStart. Service: {serviceName}");

        // 3. Marker file exists with version content. Allow up to 5s for
        //    the bash script's `read_version > $MARKER_FILE` to flush —
        //    systemd's "active" state can fire slightly before the script
        //    has written the marker, even with Type=simple.
        WaitForFileContent(fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(5)).ShouldBeTrue(
            customMessage: $"marker at {fixture.MarkerFilePath} MUST contain '1.0.0' after start — proves the script started, read version.txt, AND wrote the marker. " +
                          "If marker absent: bash script crashed at start. If marker has wrong content: version.txt wasn't read correctly.");

        // 4. Stop the service. KillMode=mixed in the unit file means
        //    SIGTERM goes to the bash main → the trap-handler deletes
        //    the marker → service exits.
        fixture.Stop(TimeSpan.FromSeconds(10));

        fixture.IsActive().ShouldBeFalse(
            customMessage: $"systemctl is-active should NOT return 'active' after Stop. Service: {serviceName}");

        // 5. Marker absent — proves the trap-handler ran (graceful stop).
        WaitForFileAbsent(fixture.MarkerFilePath, TimeSpan.FromSeconds(5)).ShouldBeTrue(
            customMessage: $"marker at {fixture.MarkerFilePath} MUST be absent after stop. " +
                          "If present: bash trap-handler didn't run → service was killed before cleanup → potential file-in-use issues for upgrade tests' Move-Item swap.");

        // 6. Uninstall (via Dispose) is idempotent + reaps unit file.
        // Dispose is called at the using's end; unit file should be gone
        // and systemctl no longer knows about the service.
    }

    [Fact]
    public void Fixture_RepeatedInstall_DoesNotFailDueToStaleService()
    {
        if (!LinuxServiceFixture.IsAvailable) return;

        var serviceName = $"squid-linux-fixture-repeat-{Guid.NewGuid():N}";
        var installDir = Path.Combine(Path.GetTempPath(), $"squid-linux-fixture-repeat-{Guid.NewGuid():N}");
        var testServiceScript = LocateTestServiceScript();

        // Round 1: install fresh.
        using (var fixture1 = new LinuxServiceFixture(serviceName, installDir))
        {
            fixture1.InstallAndStart(testServiceScript, "v1", TimeSpan.FromSeconds(15));
            fixture1.IsActive().ShouldBeTrue();
            // Dispose runs at end of using → uninstalls.
        }

        // Round 2: install AGAIN with the same service name, NEW fixture
        // instance. The previous Dispose should have cleaned up enough
        // that this works without "service exists" errors.
        using (var fixture2 = new LinuxServiceFixture(serviceName, installDir))
        {
            fixture2.InstallAndStart(testServiceScript, "v2", TimeSpan.FromSeconds(15));
            fixture2.IsActive().ShouldBeTrue();
            WaitForFileContent(fixture2.MarkerFilePath, "v2", TimeSpan.FromSeconds(5)).ShouldBeTrue(
                customMessage: "round-2 marker MUST reflect v2 content — proves the second install used its own version.txt, not stale v1");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Locates the test-service bash script at the canonical sibling
    /// project path. Mirrors the Windows project's
    /// <c>LocateTestServiceExe</c> walk-up pattern.
    /// </summary>
    private static string LocateTestServiceScript()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "Squid.LinuxTentacleE2E.TestService", "squid-linux-test-service.sh");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "Could not locate squid-linux-test-service.sh. Expected at tests/Squid.LinuxTentacleE2E.TestService/squid-linux-test-service.sh; " +
            "if running from an unusual CI layout, the walk-up depth may need to increase.");
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
            catch { /* mid-write, retry */ }
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
