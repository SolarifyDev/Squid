using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.V2;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> over a mocked Kubernetes API. V1 Start, V2 Start and a script leaving the
/// pending queue share ONE launch path: it decides launch-or-queue under the ticket lock, marks the ticket in flight while its pod is created, owns
/// the isolation mutex until the launch ends, and honours a Cancel that met the launch. Each interleaving is driven by a hook or a parked Kubernetes
/// call; a racing call that the fix makes wait on the ticket lock runs on its own thread and is observed blocked (never a timed sleep). Every wait is bounded.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceOneLaunchPathTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-onelaunch-{Guid.NewGuid():N}");

    public ScriptPodServiceOneLaunchPathTests()
    {
        DiskSpaceChecker.Enabled = false;
        Directory.CreateDirectory(_workspace);

        _tentacleSettings = new TentacleSettings { WorkspacePath = _workspace };
        _kubernetesSettings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image@sha256:abc123def456789012345678901234567890123456789012345678901234aa77",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc"
        };

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns((V1Pod pod, string ns) => pod);
        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1PodList { Items = new List<V1Pod>() });
        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>())).Returns(() => new MemoryStream());
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1Pod { Status = new V1PodStatus { Phase = "Running" } });
    }

    // ── a Start that ends up queued must not lose a Cancel that arrived during it ──

    [Theory]
    [InlineData("V1")]
    [InlineData("V2")]
    public void CancelV1_ArrivesWhileAStartIsDecidingToQueue_TheQueuedScriptIsCancelled_AndNeverLaunches(string startVersion)
    {
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        RacingCall? cancel = null;

        service.StartAdmissionHookForTests = () =>
        {
            service.StartAdmissionHookForTests = null;
            cancel = RacingCall.Run(() => service.CancelScript(new CancelScriptCommand(queued.ScriptTicket, 0)));
        };

        // The racing Cancel runs to its end the moment the Start leaves the lock: by then the script must already be in the queue for it to find.
        service.AfterStartDecisionHookForTests = () =>
        {
            service.AfterStartDecisionHookForTests = null;
            cancel!.Join();
        };

        var started = Start(service, startVersion, queued);
        service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0));

        started.ShouldBe(ProcessState.Pending, "the mutex is held by the holder, so the Start queues the script");
        service.PendingScripts.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeFalse("the Cancel must take the queued script out of the queue");
        service.IsRunningScript(queued.ScriptTicket.TaskId).ShouldBeFalse();
        _ops.Verify(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == PodOf(queued)), It.IsAny<string>()), Times.Never, "a Cancel answered Canceled must not be followed by the script's pod being created");
    }

    [Fact]
    public void StartV2_QueuedScriptCancelledByV1RightAfterTheStartDecided_AnswersPending_NeverCompleted()
    {
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);

        service.AfterStartDecisionHookForTests = () =>
        {
            service.AfterStartDecisionHookForTests = null;
            service.CancelScript(new CancelScriptCommand(queued.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        };

        var started = service.StartScript(queued);

        started.State.ShouldBe(ScriptStateV2.Pending, "the script was queued, not run: answering Completed(0) would report a script that never ran as a success");
    }

    // ── a V1 Start owns its launch: a V1 Cancel during pod creation must not release the launcher's mutex ──

    [Fact]
    public async Task CancelV1_DuringAV1StartsPodCreation_LeavesTheMutexToTheLauncher_ThenCancelsThePod_AndOnlyThenLaunchesTheNextScript()
    {
        var service = NewService();
        var launching = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        var next = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        var parked = ParkPodCreate(PodOf(launching));
        var starting = Task.Run(() => service.StartScript(ScriptV2Mapping.ToV1(launching)));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue("the V1 Start must reach pod creation");
        service.StartScript(ScriptV2Mapping.ToV1(next)).State.ShouldBe(ProcessState.Pending);

        service.CancelScript(new CancelScriptCommand(launching.ScriptTicket, 0));

        service.MutexLocks.ContainsKey(launching.ScriptTicket.TaskId).ShouldBeTrue("only the launcher may release the isolation mutex it is launching under");
        _ops.Verify(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == PodOf(next)), It.IsAny<string>()), Times.Never, "the next script must not start beside a pod still being created");

        parked.Release.Set();
        var started = await starting.WaitAsync(WaitLimit);

        started.ExitCode.ShouldBe(ScriptExitCodes.Canceled, "the launcher honours the Cancel that met its launch");
        service.GetStatus(new ScriptStatusRequest(launching.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        service.IsRunningScript(launching.ScriptTicket.TaskId).ShouldBeFalse();
        _ops.Verify(o => o.DeletePod(PodOf(launching), "test-ns", It.IsAny<int?>()), Times.Once);
        _ops.Verify(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == PodOf(next)), It.IsAny<string>()), Times.Once, "freeing the mutex after the cancelled launch starts the next script");
    }

    // ── the arbitration: a Cancel that records its request after the launcher's check, while the launch is still in flight ──

    [Theory]
    [InlineData("V1")]
    [InlineData("V2")]
    public async Task Cancel_RecordsItsRequestAfterTheLauncherCheckedWhileTheLaunchIsStillInFlight_CancelsThePodItself(string cancelVersion)
    {
        var service = NewService();
        service.LaunchSettleTimeoutForTests = TimeSpan.FromMilliseconds(50);
        var command = NewCommand();
        var parked = ParkPodCreate(PodOf(command));
        var starting = Task.Run(() => service.StartScript(command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();
        using var launcherChecked = new ManualResetEventSlim();
        using var releaseLauncher = new ManualResetEventSlim();

        // The launcher has tracked the pod, found no request and is held there (still in flight) while the Cancel records its request and arbitrates.
        service.AfterHonourCheckHookForTests = () => { service.AfterHonourCheckHookForTests = null; launcherChecked.Set(); releaseLauncher.Wait(WaitLimit); };
        service.BeforeCancelRequestRecordedHookForTests = () =>
        {
            service.BeforeCancelRequestRecordedHookForTests = null;
            parked.Release.Set();
            launcherChecked.Wait(WaitLimit).ShouldBeTrue("the launcher must reach its cancel check");
        };

        Cancel(service, cancelVersion, command);
        releaseLauncher.Set();
        await starting.WaitAsync(WaitLimit);

        // V1 reports a cancel in its answer only (no terminal result is kept), so the pod's fate is what proves the cancel was not lost.
        service.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeFalse("nobody else will ever consume a request recorded after the launcher's check");
        service.HasCancelRequestForTests(command.ScriptTicket.TaskId).ShouldBeFalse();
        _ops.Verify(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    // ── a published terminal result is never retracted ──────────────────────

    [Fact]
    public void PendingTimeout_ARacingV1CancelOfTheQueuedScript_NeverRetractsThePublishedTimeout()
    {
        _kubernetesSettings.IsolationMutexTimeoutMinutes = 0;
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);
        ScriptStatusResponse? seen = null;
        RacingCall? cancel = null;

        // Between publishing the timeout and leaving the queue, a V1 Cancel of the same ticket races the removal.
        service.BetweenPublishAndForgetHookForTests = () =>
        {
            service.BetweenPublishAndForgetHookForTests = null;
            cancel = RacingCall.Run(() => service.CancelScript(new CancelScriptCommand(queued.ScriptTicket, 0)));
            seen = service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0));
        };

        service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0));
        cancel!.Join();

        seen!.ExitCode.ShouldBe(ScriptExitCodes.Timeout, "a reader saw the published timeout");
        service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.Timeout, "a result a reader has seen must never disappear afterwards");
        service.GetStatus(new ScriptStatusRequestV2(queued.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.Timeout);
    }

    [Fact]
    public void CompleteV1_ThePodMonitorFailsTheTicketBetweenCompletesTwoTakes_CompleteStillGetsTheTicket_AndNothingIsLeftPublished()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command).State.ShouldBe(ScriptStateV2.Running);
        SetupStuckPendingPod(command);
        var monitor = new KubernetesPodMonitor(new KubernetesPodManager(_ops.Object, _kubernetesSettings), service, _tentacleSettings, _kubernetesSettings);
        RacingCall? failing = null;

        // Complete found no terminal result; the monitor now gives up on the pod (publish Timeout, then forget the tracked entry) before Complete takes it.
        service.CompleteTakeHookForTests = () =>
        {
            service.CompleteTakeHookForTests = null;
            failing = RacingCall.Run(monitor.FailPendingPods);
            SetupPodPhase("Succeeded");
        };

        var completed = service.CompleteScript(new CompleteScriptCommand(command.ScriptTicket, 0));
        failing!.Join();

        completed.ExitCode.ShouldNotBe(ScriptExitCodes.UnknownResult, "the ticket was tracked when Complete looked: it must not be answered as unknown");
        service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.UnknownResult, "the completed ticket must leave no stray result behind");
    }

    // ── readers that must see a launching script ────────────────────────────

    [Theory]
    [InlineData("V1")]
    [InlineData("V2")]
    public async Task IsRunningScript_WhileThePodIsBeingCreated_IsTrue(string startVersion)
    {
        var service = NewService();
        var command = NewCommand();
        var parked = ParkPodCreate(PodOf(command));
        var starting = Task.Run(() => Start(service, startVersion, command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();

        var running = service.IsRunningScript(command.ScriptTicket.TaskId);
        parked.Release.Set();
        await starting.WaitAsync(WaitLimit);

        running.ShouldBeTrue("a script whose pod is being created is running: the disk-pressure heal must not reclaim its workspace");
    }

    [Theory]
    [InlineData("V1")]
    [InlineData("V2")]
    public async Task StartV1_DuplicateWhileThePodIsBeingCreated_CreatesOnePod_AndReportsPending(string firstVersion)
    {
        var service = NewService();
        var command = NewCommand();
        var parked = ParkFirstPodCreate(PodOf(command));
        var starting = Task.Run(() => Start(service, firstVersion, command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();

        var duplicate = service.StartScript(ScriptV2Mapping.ToV1(command));
        parked.Release.Set();
        await starting.WaitAsync(WaitLimit);

        duplicate.State.ShouldBe(ProcessState.Pending);
        _ops.Verify(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == PodOf(command)), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task StartV1_DuplicateWhileAQueuedScriptIsBeingLaunched_ReportsPending_AndIsNotQueuedASecondTime()
    {
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "onelaunch-mx");
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);
        var parked = ParkFirstPodCreate(PodOf(queued));
        var releasing = Task.Run(() => service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0)));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue("the holder's release must dequeue the queued script");

        var duplicate = service.StartScript(ScriptV2Mapping.ToV1(queued));
        parked.Release.Set();
        await releasing.WaitAsync(WaitLimit);

        duplicate.State.ShouldBe(ProcessState.Pending);
        service.PendingScripts.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeFalse("a duplicate Start of a launching script must not queue a second launch");
        _ops.Verify(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == PodOf(queued)), It.IsAny<string>()), Times.Once);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A call racing the test thread: it runs on its own thread, and Run returns once it has finished or is blocked waiting (on a lock).</summary>
    private sealed class RacingCall
    {
        private readonly Thread _thread;
        private Exception? _failure;

        private RacingCall(Action call) => _thread = new Thread(() => { try { call(); } catch (Exception ex) { _failure = ex; } }) { IsBackground = true };

        public static RacingCall Run(Action call)
        {
            var racing = new RacingCall(call);
            var waited = Stopwatch.StartNew();

            racing._thread.Start();

            while (racing._thread.IsAlive && (racing._thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && waited.Elapsed < WaitLimit) Thread.Yield();

            return racing;
        }

        public void Join()
        {
            _thread.Join(WaitLimit).ShouldBeTrue("the racing call must finish");
            _failure.ShouldBeNull();
        }
    }

    private sealed record Park(ManualResetEventSlim Entered, ManualResetEventSlim Release);

    private void SetupPodPhase(string phase) => _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1Pod { Status = new V1PodStatus { Phase = phase } });

    // The pod has sat in Pending for 10 minutes: the pod monitor gives up on it.
    private void SetupStuckPendingPod(StartScriptCommandV2 command)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = PodOf(command), CreationTimestamp = DateTime.UtcNow.AddMinutes(-10), Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = command.ScriptTicket.TaskId } },
            Status = new V1PodStatus { Phase = "Pending" }
        };

        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1PodList { Items = new List<V1Pod> { pod } });
        SetupPodPhase("Pending");
    }

    private ScriptPodService NewService() => new(_tentacleSettings, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings));

    private static ProcessState Start(ScriptPodService service, string version, StartScriptCommandV2 command)
        => version == "V1" ? service.StartScript(ScriptV2Mapping.ToV1(command)).State : ToProcessState(service.StartScript(command).State);

    private static ProcessState ToProcessState(ScriptStateV2 state) => state switch
    {
        ScriptStateV2.Pending => ProcessState.Pending,
        ScriptStateV2.Running => ProcessState.Running,
        _ => ProcessState.Complete
    };

    private static void Cancel(ScriptPodService service, string version, StartScriptCommandV2 command)
    {
        if (version == "V1") service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));
        else service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
    }

    private static string PodOf(StartScriptCommandV2 command) => $"squid-script-{command.ScriptTicket.TaskId[..12]}";

    private static StartScriptCommandV2 NewCommand(ScriptIsolationLevel isolation = ScriptIsolationLevel.NoIsolation, string? mutexName = null)
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = "echo hi", Isolation = isolation, IsolationMutexName = mutexName, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    private Park ParkPodCreate(string podName)
    {
        var park = new Park(new ManualResetEventSlim(), new ManualResetEventSlim());

        _ops.Setup(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == podName), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            park.Entered.Set();
            park.Release.Wait(WaitLimit);

            return pod;
        });

        return park;
    }

    // Only the first creation of this pod parks; a second one (a duplicate launch) returns at once so the test fails fast instead of hanging.
    private Park ParkFirstPodCreate(string podName)
    {
        var park = new Park(new ManualResetEventSlim(), new ManualResetEventSlim());
        var calls = 0;

        _ops.Setup(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == podName), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            if (Interlocked.Increment(ref calls) > 1) return pod;

            park.Entered.Set();
            park.Release.Wait(WaitLimit);

            return pod;
        });

        return park;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
