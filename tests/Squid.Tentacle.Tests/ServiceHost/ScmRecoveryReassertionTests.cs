using System.ComponentModel;
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

    // ── Live owner: unreadable counts as alive, and a live owner is WATCHED ──

    [Fact]
    public void UnreadableOwner_AccessDenied_IsTreatedAsAlive_NotAsDead()
    {
        var path = WriteMarker("squid-tentacle", 1, ownerPid: 4242);
        var calls = 0;

        var restored = ScmRecoveryReassertion.Run(_dir, _ => throw new Win32Exception(5), (_, _) => { calls++; return 0; });

        restored.ShouldBe(0);
        calls.ShouldBe(0, "Access Denied reading the owner's start time must not read as 'owner dead' - that would re-enable the flag mid-upgrade");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void OwnerLiveness_DistinguishesDeadAliveAndUnreadable()
    {
        FailureFlagMarker.TryParse(MarkerJson("svc", 1, 7), out var marker).ShouldBeTrue();

        marker.GetOwnerLiveness(_ => throw new UnauthorizedAccessException()).ShouldBe(OwnerLiveness.Unreadable);
        marker.GetOwnerLiveness(_ => throw new Win32Exception(5)).ShouldBe(OwnerLiveness.Unreadable);
        marker.GetOwnerLiveness(OwnerGone).ShouldBe(OwnerLiveness.Dead);
        marker.GetOwnerLiveness(_ => OwnerStart).ShouldBe(OwnerLiveness.Alive);
    }

    [Fact]
    public async Task LiveOwnerKilledAfterTheAgentStarted_FlagIsRestoredWhenItExits()
    {
        var path = WriteMarker("squid-tentacle", 1, ownerPid: 4242);
        var ownerAlive = true;
        var polls = 0;
        var flagCalls = new List<(string, bool)>();

        var restored = await ScmRecoveryReassertion.RunAndWatchAsync(_dir, _ => ownerAlive ? OwnerStart : null, (svc, enabled) => { flagCalls.Add((svc, enabled)); return 0; },
            (_, _) => { if (++polls == 3) ownerAlive = false; return Task.CompletedTask; }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        restored.ShouldBe(1);
        polls.ShouldBe(3, "the watcher must keep re-probing while the owner lives and stop as soon as it is handled");
        flagCalls.ShouldBe(new[] { ("squid-tentacle", true) });
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task LiveOwnerFinishesNormally_MarkerGone_NothingIsRestored()
    {
        var path = WriteMarker("squid-tentacle", 1, ownerPid: 4242);
        var calls = 0;

        var restored = await ScmRecoveryReassertion.RunAndWatchAsync(_dir, _ => OwnerStart, (_, _) => { calls++; return 0; },
            (_, _) => { File.Delete(path); return Task.CompletedTask; }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        restored.ShouldBe(0);
        calls.ShouldBe(0, "the script's own finally removed the marker; the watcher must not touch the flag");
    }

    [Fact]
    public async Task Watcher_StopsWhenTheAgentIsStopping()
    {
        WriteMarker("squid-tentacle", 1, ownerPid: 4242);
        using var cts = new CancellationTokenSource();

        var watch = ScmRecoveryReassertion.RunAndWatchAsync(_dir, _ => OwnerStart, (_, _) => 0, async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); }, cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => watch.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task OrphanedMarkerNextToALiveOne_IsRestoredImmediately_WhileTheLiveOneIsWatched()
    {
        WriteMarker("svc-live", 1, ownerPid: 11);
        WriteMarker("svc-dead", 1, ownerPid: 12);
        var calls = new List<string>();
        var callsAtFirstPoll = -1;
        var livePath = Path.Combine(_dir, FailureFlagMarker.FilePrefix + "svc-live" + FailureFlagMarker.FileExtension);

        var restored = await ScmRecoveryReassertion.RunAndWatchAsync(_dir, pid => pid == 11 ? OwnerStart : null, (svc, _) => { calls.Add(svc); return 0; },
            (_, _) => { callsAtFirstPoll = calls.Count; File.Delete(livePath); return Task.CompletedTask; }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        restored.ShouldBe(1);
        callsAtFirstPoll.ShouldBe(1, "the orphan is restored in the first pass, before the watcher's first wait");
        calls.ShouldBe(new[] { "svc-dead" });
    }

    // ── Isolation: one marker never stops the others ────────────────────────

    [Fact]
    public void ThrowingLookupForOneMarker_DoesNotAbortTheRemainingMarkers()
    {
        WriteMarker("svc-a", 1, ownerPid: 11);
        WriteMarker("svc-b", 1, ownerPid: 12);
        var restoredServices = new List<string>();

        var restored = ScmRecoveryReassertion.Run(_dir, pid => pid == 11 ? throw new InvalidOperationException("boom") : null, (svc, _) => { restoredServices.Add(svc); return 0; });

        restored.ShouldBe(1);
        restoredServices.ShouldBe(new[] { "svc-b" });
    }

    [Fact]
    public void MarkerThatCannotBeDeleted_StillCountsAsRestored_AndOtherMarkersAreProcessed()
    {
        var first = WriteMarker("svc-a", 1, ownerPid: 11);
        WriteMarker("svc-b", 1, ownerPid: 12);
        var restoredServices = new List<string>();

        using var undeletable = MakeUndeletable(first);
        var restored = ScmRecoveryReassertion.Run(_dir, OwnerGone, (svc, _) => { restoredServices.Add(svc); return 0; });

        restored.ShouldBe(2, "the flag WAS restored for both; a delete failure must not abort the loop or hide the restore");
        restoredServices.ShouldBe(new[] { "svc-a", "svc-b" }, ignoreOrder: true);
    }

    /// <summary>Makes <c>File.Delete</c> throw for real: a sharing violation on Windows, a read-only directory elsewhere. Disposing undoes it.</summary>
    private IDisposable MakeUndeletable(string markerPath)
    {
        if (OperatingSystem.IsWindows()) return new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        return new Restore(() => File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;

        public Restore(Action undo) => _undo = undo;

        public void Dispose() => _undo();
    }

    // ── Wiring: the agent actually calls it at startup, off the start path ──

    [Fact]
    public async Task HostedService_StartsTheReassertion_WithTheStoppingToken_WithoutWaitingForIt()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverFinishes = new TaskCompletionSource();
        var reported = new List<int>();
        var service = new TentacleScmHostedService(new[] { "no-such-command-scm-e2e" }, Mock.Of<IHostApplicationLifetime>(), reported.Add, ct => { started.TrySetResult(ct); return neverFinishes.Task; });
        using var cts = new CancellationTokenSource();

        await InvokeExecuteAsync(service, cts.Token).WaitAsync(TimeSpan.FromSeconds(30));

        reported.Count.ShouldBe(1, "a re-assert that never completes (hung sc.exe / live-owner watcher) must not block the agent pipeline");
        (await started.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(cts.Token);
    }

    [Fact]
    public void HostedService_DefaultReassertion_IsTheProductionHostWiring()
    {
        // The Windows-only production path cannot run here, so pin the seam: with no injected delegate the service must
        // be wired to RunForThisHostAsync (deleting that default would silently disable the self-heal everywhere).
        var service = new TentacleScmHostedService(Array.Empty<string>(), Mock.Of<IHostApplicationLifetime>());
        var field = typeof(TentacleScmHostedService).GetField("_reassertScmRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var wired = (Func<CancellationToken, Task>)field.GetValue(service)!;

        wired.Method.ShouldBe(typeof(ScmRecoveryReassertion).GetMethod(nameof(ScmRecoveryReassertion.RunForThisHostAsync))!);
    }

    [Fact]
    public async Task HostedService_ReassertionThatThrows_DoesNotFailTheAgent()
    {
        var reported = new List<int>();
        var service = new TentacleScmHostedService(new[] { "no-such-command-scm-e2e" }, Mock.Of<IHostApplicationLifetime>(), reported.Add, _ => throw new InvalidOperationException("boom"));

        await InvokeExecuteAsync(service, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        reported.Count.ShouldBe(1);
    }

    // ── sc.exe: bounded wait ─────────────────────────────────────────────────

    [Fact]
    public void WaitBounded_ChildThatNeverExits_IsKilledAndReportedFailed_WithinTheBound()
    {
        using var child = StartSleeper(seconds: 60);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var exit = WindowsServiceHost.WaitBounded(child, TimeSpan.FromMilliseconds(500));

        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        exit.ShouldBe(1);
        child.WaitForExit(TimeSpan.FromSeconds(10)).ShouldBeTrue("a hung sc.exe must be killed, not leaked (Kill is asynchronous, so allow it to land)");
    }

    [Fact]
    public void WaitBounded_ChildThatExits_ReturnsItsExitCode()
    {
        using var child = StartExiting(exitCode: 3);

        WindowsServiceHost.WaitBounded(child, TimeSpan.FromSeconds(30)).ShouldBe(3);
    }

    private static System.Diagnostics.Process StartSleeper(int seconds) => Start(OperatingSystem.IsWindows() ? "ping" : "sleep", OperatingSystem.IsWindows() ? new[] { "-n", (seconds + 1).ToString(), "127.0.0.1" } : new[] { seconds.ToString() });

    private static System.Diagnostics.Process StartExiting(int exitCode) => OperatingSystem.IsWindows() ? Start("cmd", new[] { "/c", $"exit {exitCode}" }) : Start("sh", new[] { "-c", $"exit {exitCode}" });

    private static System.Diagnostics.Process Start(string file, string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        return System.Diagnostics.Process.Start(psi)!;
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
        File.WriteAllText(path, MarkerJson(service, originalFlag, ownerPid));

        return path;
    }

    private static Task InvokeExecuteAsync(TentacleScmHostedService service, CancellationToken ct)
    {
        var method = typeof(TentacleScmHostedService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (Task)method.Invoke(service, new object[] { ct })!;
    }

    private static string MarkerJson(string service, int originalFlag, int ownerPid) => $"{{\"service\":\"{service}\",\"originalFlag\":{originalFlag},\"ownerPid\":{ownerPid},\"ownerStartUtcTicks\":{OwnerStart.Ticks}}}";
}
