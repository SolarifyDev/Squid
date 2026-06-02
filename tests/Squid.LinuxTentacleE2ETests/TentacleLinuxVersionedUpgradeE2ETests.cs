using System.Security.Cryptography;
using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Real-systemd E2E for the blue-green (versioned-layout) upgrade path of
/// <c>upgrade-linux-tentacle.sh</c>. Stages a v1 service in the versioned layout
/// (<c>versions/&lt;v1&gt;/Squid.Tentacle</c> selected by a <c>current</c> symlink,
/// unit ExecStart through the pointer), then drives the production upgrade script
/// end-to-end (systemd-run --scope → repoint <c>current</c> → systemctl restart →
/// healthz).
///
/// <para>The defining guarantee under test: the upgrade <b>never touches the
/// directory of the version that is running</b>. Both tests capture the v1 binary's
/// SHA256 before the upgrade and assert it is byte-for-byte unchanged afterward —
/// on the happy path (v1 superseded by v2 but preserved) AND on the rollback path
/// (v1 restored by repointing <c>current</c> back).</para>
///
/// <para>Tier: 🟢 H (Rule 12.4) — real production .sh, real systemd unit, real
/// systemd-run scope, real systemctl restart, real python3 /healthz responder,
/// real symlink repoint. No mocks at the OS-resource layer.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.UpgradeLifecycle)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxVersionedUpgradeE2ETests
{
    private const string V1 = "1.0.0-test";
    private const string V2 = "2.0.0-test";

    // ========================================================================
    // Versioned happy path — current repoints v1→v2, v1 dir byte-unchanged.
    // ========================================================================

    [Fact]
    public void Versioned_HappyPath_RepointsCurrentToV2_AndLeavesV1Intact()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        ctx.Fixture.InstallVersionedAndStart(
            ctx.TestServiceScript,
            initialVersion: V1,
            startTimeout: TimeSpan.FromSeconds(15),
            extraEnvironment: new Dictionary<string, string> { ["SQUID_TEST_SERVICE_HEALTHZ"] = "1" });

        WaitForFileContent(ctx.Fixture.VersionedMarkerPath(V1), V1, TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running (per-version marker) before the versioned upgrade can be validated");

        var v1Binary = Path.Combine(ctx.Fixture.VersionDir(V1), "Squid.Tentacle");
        var v1HashBefore = Sha256Hex(v1Binary);

        ctx.Mirror.StagePreBuiltArchive(ctx.BuildV2BundleTarGz(targetVersion: V2));
        var (exitCode, output) = ctx.RunUpgradeScript(ctx.RenderProductionScriptForVersion(targetVersion: V2));

        exitCode.ShouldBe(0, customMessage: Tail("versioned happy-path upgrade MUST exit 0", exitCode, output));

        var status = ctx.ReadLastUpgradeStatus();
        status.ShouldNotBeNull(customMessage: $"last-upgrade.json MUST be written. Path: {ctx.StatusFilePath}");
        status.Status.ShouldBe("SUCCESS", customMessage: $"status MUST be SUCCESS. Got '{status.Status}'. Detail: {status.Detail}");

        // `current` now resolves to versions/<V2> (atomic repoint landed).
        ReadThroughCurrent(ctx, "version.txt").ShouldBe(V2,
            customMessage: "current/version.txt MUST be V2 — the `current` pointer was repointed to versions/<V2>");

        // The previous version directory is still present AND byte-for-byte unchanged:
        // the blue-green swap staged V2 into a SEPARATE versions/<V2> dir and only
        // repointed the pointer — it never moved/overwrote/deleted versions/<V1>.
        Directory.Exists(ctx.Fixture.VersionDir(V1)).ShouldBeTrue(
            "versions/<V1> MUST remain after a blue-green upgrade (failure isolation — old version preserved)");
        Sha256Hex(v1Binary).ShouldBe(v1HashBefore,
            "the previous version's binary MUST be byte-for-byte unchanged — the upgrade never touches the running version's directory");

        WaitForFileContent(ctx.Fixture.VersionedMarkerPath(V2), V2, TimeSpan.FromSeconds(30)).ShouldBeTrue(
            "after repoint + restart, the V2 service MUST write its per-version marker (proves it started on the new version through `current`)");

        ctx.Fixture.IsActive().ShouldBeTrue(customMessage: "service MUST still be active after the versioned upgrade");

        ctx.MarkClean();
    }

    // ========================================================================
    // Versioned rollback — V2 healthz fails, current repoints back to V1,
    // V1 dir byte-unchanged (it was never touched), service runs V1 again.
    // ========================================================================

    [Fact]
    public void Versioned_HealthzFails_RollsBackByRepointingCurrentToV1()
    {
        if (!LinuxLifecycleContext.IsAvailable) return;

        using var ctx = new LinuxLifecycleContext();

        ctx.Fixture.InstallVersionedAndStart(
            ctx.TestServiceScript,
            initialVersion: V1,
            startTimeout: TimeSpan.FromSeconds(15),
            extraEnvironment: new Dictionary<string, string> { ["SQUID_TEST_SERVICE_HEALTHZ"] = "1" });

        WaitForFileContent(ctx.Fixture.VersionedMarkerPath(V1), V1, TimeSpan.FromSeconds(15)).ShouldBeTrue(
            "v1 service must be running before the rollback scenario can be validated");

        var v1Binary = Path.Combine(ctx.Fixture.VersionDir(V1), "Squid.Tentacle");
        var v1HashBefore = Sha256Hex(v1Binary);

        // failHealthz=true → V2's /healthz responder returns 503 → .sh's Phase B
        // healthcheck loop exhausts → rollback fires.
        ctx.Mirror.StagePreBuiltArchive(ctx.BuildV2BundleTarGz(targetVersion: V2, failHealthz: true));
        var (exitCode, output) = ctx.RunUpgradeScript(ctx.RenderProductionScriptForVersion(targetVersion: V2));

        // Exit 4 is the documented ROLLED_BACK code (upgrade-linux-tentacle.sh header).
        exitCode.ShouldBe(4, customMessage: Tail("versioned rollback MUST exit 4 (ROLLED_BACK)", exitCode, output));

        var status = ctx.ReadLastUpgradeStatus();
        status.ShouldNotBeNull(customMessage: $"last-upgrade.json MUST be written. Path: {ctx.StatusFilePath}");
        status.Status.ShouldBe("ROLLED_BACK",
            customMessage: $"status MUST be ROLLED_BACK after healthz failure. Got '{status.Status}'. Detail: {status.Detail}");

        // `current` was repointed BACK to versions/<V1>.
        ReadThroughCurrent(ctx, "version.txt").ShouldBe(V1,
            customMessage: "current/version.txt MUST be back to V1 — rollback repointed `current` to the previous version");

        // V1 binary byte-for-byte unchanged: the rollback is a pointer flip, and the
        // V1 directory was never touched during the (failed) upgrade attempt.
        Sha256Hex(v1Binary).ShouldBe(v1HashBefore,
            "the previous version's binary MUST be byte-for-byte unchanged through a failed upgrade + rollback");

        WaitForFileContent(ctx.Fixture.VersionedMarkerPath(V1), V1, TimeSpan.FromSeconds(30)).ShouldBeTrue(
            "the V1 service MUST be running again after rollback (current repointed back + restart)");

        ctx.Fixture.IsActive().ShouldBeTrue(customMessage: "service MUST be active on the previous version after rollback");

        ctx.MarkClean();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string ReadThroughCurrent(LinuxLifecycleContext ctx, string fileName)
    {
        var path = Path.Combine(ctx.Fixture.CurrentPointer, fileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "(absent)";
    }

    private static string Sha256Hex(string path)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
    }

    private static bool WaitForFileContent(string path, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == expected) return true;
            Thread.Sleep(200);
        }

        return false;
    }

    private static string Tail(string prefix, int exitCode, string output) =>
        $"{prefix}. Got exit {exitCode}.\noutput tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}";
}
