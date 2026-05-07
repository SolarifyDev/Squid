using Squid.Core.Services.Machines.Upgrade;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.L.E.4 — first real lifecycle E2E on the Linux side. Mirrors
/// the J.E.3 Windows lifecycle suite scoping discipline: start with
/// the SIMPLEST scenarios that don't need systemd / Phase B, surface
/// any production .sh bugs, then layer on full lifecycle in subsequent
/// phases.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Production .sh loaded
/// from disk verbatim via <c>LinuxTentacleUpgradeStrategy.BuildScript</c>;
/// real <see cref="LocalReleaseMirror"/> serving real HTTP; real
/// <c>curl</c> + <c>tar</c> on the agent side; only the upstream
/// GitHub Releases CDN is replaced.</para>
///
/// <para><b>This phase covers Phase A only</b> — the .sh's pre-scope
/// flow (download, SHA verify, extract, ldd check). Phase B
/// (<c>systemd-run --scope</c> + <c>systemctl restart</c>) requires
/// LinuxServiceFixture-installed unit + sudo and lands in J.L.E.5+.
///
/// Coverage map this phase:
/// <list type="bullet">
///   <item>E1.u1-Linux: download 404 → exit 6 + FAILED status with
///         "Target tarball not reachable" detail</item>
/// </list></para>
///
/// <para><b>Skip-on-non-Linux + skip-on-no-sudo</b>: composes
/// <see cref="LinuxLifecycleContext.IsAvailable"/>. macOS / Windows
/// dev hosts no-op-skip; Linux without passwordless sudo also skips.
/// CI <c>ubuntu-latest</c> runner has both.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.UpgradeLifecycle)]
public sealed class TentacleLinuxUpgradeLifecycleE2ETests
{
    // ========================================================================
    // E1.u1-Linux — Download URL 404 → exit 6 + FAILED status with download detail
    //
    // Linux analog of Windows E1u1. The .sh's tarball-download HEAD probe
    // fires `exit 6` if `curl --fail` returns non-2xx (404 in our test).
    // BEFORE the systemd-run --scope detach — proves Phase A's pre-scope
    // error path writes status + exits cleanly.
    //
    // Why E1.u1 first (not E1.h): the happy-path full lifecycle requires
    // Phase B's systemctl restart against a real running service, which
    // needs LinuxServiceFixture wired up. E1.u1 only needs the mirror to
    // be reachable AND configured to 404 — surfaces the Phase A error-
    // handling code path that real operators hit when:
    //   - Tarball isn't published yet (recent tag, build pipeline still running)
    //   - Air-gap mirror missing the version
    //   - Firewall blocks GitHub Releases for the agent host
    //   - Operator typo'd a version number
    // ========================================================================

    [Fact]
    public void E1u1_DownloadVersionNotFound_ExitsSixAndWritesFailedStatusWithDownloadDetail()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        // Mirror configured to 404 the target version.
        ctx.Mirror.ConfigureNotFoundForVersion("9.99.99-not-released");

        var script = ctx.RenderProductionScriptForVersion(targetVersion: "9.99.99-not-released");
        var (exitCode, output) = ctx.RunUpgradeScript(script);

        // .sh's tarball-method HEAD-probe-fail path emits exit 6.
        // Documented in upgrade-linux-tentacle.sh header: "6 — target
        // tarball URL not reachable (tarball method only)".
        exitCode.ShouldBe(6,
            customMessage: $"download 404 MUST produce exit 6 (documented in upgrade-linux-tentacle.sh header). " +
                          $"Got exit {exitCode}. " +
                          (exitCode == 2 ? "Got exit 2 (download failure during the actual download, not the HEAD probe) — different code path; the .sh fell THROUGH the HEAD probe. " : "") +
                          (exitCode == 14 ? "Got exit 14 (no install method succeeded) — apt/yum probes ran instead of falling through to tarball-only render. " : "") +
                          $"output:\n{output}");

        // last-upgrade.json reports FAILED with the operator-actionable detail.
        var statusPayload = ctx.ReadLastUpgradeStatus();
        statusPayload.ShouldNotBeNull(
            customMessage: $"last-upgrade.json MUST be written even on Phase A error path — operators must see WHY the upgrade failed via the UI, not just 'no status'. Path: {ctx.StatusFilePath}");

        statusPayload.Status.ShouldBe("FAILED",
            customMessage: $"download 404 status MUST be 'FAILED'. Got: '{statusPayload.Status}'");

        statusPayload.Detail.ShouldContain("not reachable",
            customMessage: $"detail MUST contain 'not reachable' so operators distinguish a download failure from generic 'unknown error'. Got: '{statusPayload.Detail}'");

        // The mirror SHOULD have received the HEAD probe attempt (curl
        // --fsSI is the .sh's probe — it counts as a request from the
        // mirror's perspective). Mirror returned 404, .sh exited.
        ctx.Mirror.ReceivedRequests.ShouldNotBeEmpty(
            customMessage: "mirror MUST have received the HEAD probe — proves the .sh actually attempted the download (vs failing earlier in Phase A and never reaching the network)");

        ctx.MarkClean();
    }
}
