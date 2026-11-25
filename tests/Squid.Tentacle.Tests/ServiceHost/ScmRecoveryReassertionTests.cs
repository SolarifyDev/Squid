using System.Reflection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using Squid.Tentacle.Core;
using Squid.Tentacle.ServiceHost;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ServiceHost;

/// <summary>
/// An upgrade that is hard-killed mid-swap never runs its <c>finally</c>, leaving the SCM failure-actions flag
/// suspended (0) so fatal agent errors stop auto-restarting forever. The agent re-applies the flag the upgrade recorded
/// (in a marker file) when it starts and the marker's owner process is gone. These tests EXECUTE that logic against real
/// marker files; the real <c>sc.exe</c> + SCM path is covered by the Windows E2E suite.
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class ScmRecoveryReassertionTests : IDisposable
{
    private static readonly DateTime OwnerStart = new(2026, 11, 25, 3, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Directory.CreateTempSubdirectory("squid-scm-reassert-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData(1, true)]    // flag was on -> back on
    [InlineData(0, false)]   // operator deliberately had it off -> stays off (NOT overwritten with 1)
    public void OrphanedMarker_RestoresTheRecordedOriginal_AndRemovesTheMarker(int originalFlag, bool expectedEnabled)
    {
        var path = WriteMarker("squid-tentacle", originalFlag, ownerPid: 4242);
        var calls = new List<(string Service, bool Enabled)>();

        var restored = ScmRecoveryReassertion.Run(_dir, OwnerGone, (svc, enabled) => { calls.Add((svc, enabled)); return 0; });

        restored.ShouldBe(1);
        calls.ShouldBe(new[] { ("squid-tentacle", expectedEnabled) });
        File.Exists(path).ShouldBeFalse("a restored marker must be removed so the next start does nothing");
    }

    [Fact]
    public void MarkerOfALiveUpgrade_IsLeftAlone_BecauseTheUpgradeIsDeliberatelyHoldingTheFlagOff()
    {
        var path = WriteMarker("squid-tentacle", 1, ownerPid: 4242);
        var calls = 0;

        var restored = ScmRecoveryReassertion.Run(_dir, pid => pid == 4242 ? OwnerStart : null, (_, _) => { calls++; return 0; });

        restored.ShouldBe(0);
        calls.ShouldBe(0, "re-enabling the flag while an upgrade is mid-swap would let SCM restart a failing binary under it");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void RecycledPid_WithADifferentStartTime_CountsAsDeadOwner()
    {
        WriteMarker("squid-tentacle", 1, ownerPid: 4242);
        var calls = 0;

        ScmRecoveryReassertion.Run(_dir, _ => OwnerStart.AddHours(5), (_, _) => { calls++; return 0; });

        calls.ShouldBe(1);
    }

    [Fact]
    public void FailedScCall_KeepsTheMarker_SoTheNextStartRetries()
    {
        var path = WriteMarker("squid-tentacle", 1, ownerPid: 4242);

        var restored = ScmRecoveryReassertion.Run(_dir, OwnerGone, (_, _) => 5);

        restored.ShouldBe(0);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void NoMarker_TouchesNothing_SoAnOperatorsFlagIsNeverOverwrittenWithoutAnInterruptedUpgrade()
    {
        var calls = 0;

        ScmRecoveryReassertion.Run(_dir, OwnerGone, (_, _) => { calls++; return 0; }).ShouldBe(0);
        ScmRecoveryReassertion.Run(Path.Combine(_dir, "missing"), OwnerGone, (_, _) => { calls++; return 0; }).ShouldBe(0);

        calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"service\":\"s\",\"originalFlag\":7,\"ownerPid\":1,\"ownerStartUtcTicks\":1}")]
    [InlineData("{\"service\":\"s\",\"originalFlag\":1,\"ownerPid\":0,\"ownerStartUtcTicks\":1}")]
    public void UnparsableMarker_IsSkipped_NeverGuessed(string content)
    {
        File.WriteAllText(Path.Combine(_dir, FailureFlagMarker.FilePrefix + "x" + FailureFlagMarker.FileExtension), content);
        var calls = 0;

        ScmRecoveryReassertion.Run(_dir, OwnerGone, (_, _) => { calls++; return 0; }).ShouldBe(0);

        calls.ShouldBe(0);
    }

    [Fact]
    public void SeveralMarkers_EachRestoredForItsOwnService()
    {
        WriteMarker("svc-a", 1, ownerPid: 11);
        WriteMarker("svc-b", 0, ownerPid: 12);
        var calls = new List<(string, bool)>();

        ScmRecoveryReassertion.Run(_dir, OwnerGone, (svc, enabled) => { calls.Add((svc, enabled)); return 0; }).ShouldBe(2);

        calls.ShouldBe(new[] { ("svc-a", true), ("svc-b", false) }, ignoreOrder: true);
    }

    [Fact]
    public void PowerShellProducerShape_ParsesIntoTheConsumer()
    {
        // The exact shape `ConvertTo-Json -Compress` emits for the hashtable in Write-FailureFlagMarker
        // (key order is irrelevant; PowerShell does not guarantee it).
        var json = "{\"ownerStartUtcTicks\":639000000000000000,\"ownerPid\":4242,\"originalFlag\":0,\"service\":\"Squid Tentacle\"}";

        FailureFlagMarker.TryParse(json, out var marker).ShouldBeTrue();

        marker.Service.ShouldBe("Squid Tentacle");
        marker.OriginalFlag.ShouldBe(0);
        marker.OwnerPid.ShouldBe(4242);
        marker.OwnerStartUtcTicks.ShouldBe(639000000000000000L);
    }

    // ── Wiring: the agent actually calls it at startup (a guard nobody calls proves nothing) ──

    [Fact]
    public async Task HostedService_ReassertsRecoveryPolicy_BeforeRunningThePipeline()
    {
        var order = new List<string>();
        var service = new TentacleScmHostedService(new[] { "no-such-command-scm-e2e" }, Mock.Of<IHostApplicationLifetime>(), _ => order.Add("report"), () => order.Add("reassert"));

        await InvokeExecuteAsync(service);

        order.ShouldBe(new[] { "reassert", "report" });
    }

    // ── sc.exe argv ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public void BuildScFailureFlagArgs_CarriesTheRequestedValue(bool enabled, string expected)
    {
        WindowsServiceHost.BuildScFailureFlagArgs("squid-tentacle", enabled).ShouldBe(new[] { "failureflag", "squid-tentacle", expected });
    }

    private static DateTime? OwnerGone(int pid) => null;

    private string WriteMarker(string service, int originalFlag, int ownerPid)
    {
        var path = Path.Combine(_dir, FailureFlagMarker.FilePrefix + service + FailureFlagMarker.FileExtension);
        var json = $"{{\"service\":\"{service}\",\"originalFlag\":{originalFlag},\"ownerPid\":{ownerPid},\"ownerStartUtcTicks\":{OwnerStart.Ticks}}}";

        File.WriteAllText(path, json);

        return path;
    }

    private static Task InvokeExecuteAsync(TentacleScmHostedService service)
    {
        var method = typeof(TentacleScmHostedService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (Task)method.Invoke(service, new object[] { CancellationToken.None })!;
    }
}
