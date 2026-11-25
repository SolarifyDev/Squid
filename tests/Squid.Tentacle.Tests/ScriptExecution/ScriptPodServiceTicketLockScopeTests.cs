using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using k8s.Models;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.V2;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> over a mocked Kubernetes API, where a pod operation on one
/// ticket is parked on demand. The ticket lock is one of 64 shared stripes, so waiting on a pod (termination, creation) while
/// holding it stalls every unrelated ticket that hashes to the same stripe; each test parks one ticket and proves a second ticket in
/// the SAME stripe is still served, with bounded waits so a regression fails instead of hanging. The correctness tests pin what the
/// narrower lock must still guarantee: one pod per ticket, one release per ticket, a Cancel that is not lost, a queued script that
/// is launched when the lock holder completes.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceTicketLockScopeTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ServedWithin = TimeSpan.FromSeconds(5);

    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-lockscope-{Guid.NewGuid():N}");

    public ScriptPodServiceTicketLockScopeTests()
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
        SetupPodsSucceeded();
    }

    // ── a parked ticket must not stall an unrelated ticket in the same lock stripe ──

    [Theory]
    [InlineData("Start")]
    [InlineData("Complete")]
    [InlineData("Cancel")]
    public async Task SlowTerminationOnOneTicket_DoesNotBlockAnotherTicketInTheSameLockSlot(string operation)
    {
        var service = NewService();
        var slow = NewCommand();
        var other = NewCommandInSameLockSlotAs(slow);
        service.StartScript(slow).State.ShouldBe(ScriptStateV2.Running);
        if (operation != "Start") service.StartScript(other).State.ShouldBe(ScriptStateV2.Running);

        var parked = ParkPodStatusRead(PodOf(slow));
        var completing = Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(slow.ScriptTicket, 0)));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue("the slow ticket's Complete must reach the pod termination wait");

        var served = await IsServedWithinAsync(() => Run(service, operation, other));
        parked.Release.Set();
        await completing;

        served.ShouldBeTrue($"{operation} of an unrelated ticket in the same lock slot waited for another ticket's pod termination");
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("Complete")]
    [InlineData("Cancel")]
    public async Task SlowPodLaunchOnOneTicket_DoesNotBlockAnotherTicketInTheSameLockSlot(string operation)
    {
        var service = NewService();
        var slow = NewCommand();
        var other = NewCommandInSameLockSlotAs(slow);
        if (operation != "Start") service.StartScript(other).State.ShouldBe(ScriptStateV2.Running);

        var parked = ParkPodCreate(PodOf(slow));
        var starting = Task.Run(() => service.StartScript(slow));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue("the slow ticket's Start must reach pod creation");

        var served = await IsServedWithinAsync(() => Run(service, operation, other));
        parked.Release.Set();
        await starting;

        served.ShouldBeTrue($"{operation} of an unrelated ticket in the same lock slot waited for another ticket's pod creation");
    }

    [Theory]
    [InlineData("Complete", "Start")]
    [InlineData("Complete", "Complete")]
    [InlineData("Complete", "Cancel")]
    [InlineData("Cancel", "Start")]
    [InlineData("Cancel", "Complete")]
    [InlineData("Cancel", "Cancel")]
    public async Task QueuedLaunchTriggeredByTheHoldersRelease_DoesNotBlockAnotherTicketInTheHoldersLockSlot(string trigger, string operation)
    {
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        var other = NewCommandInSameLockSlotAs(holder);
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);
        if (operation != "Start") service.StartScript(other).State.ShouldBe(ScriptStateV2.Running);

        var parked = ParkPodCreate(PodOf(queued));
        var completing = Task.Run(() => Run(service, trigger, holder));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue("releasing the holder must launch the queued script");

        var served = await IsServedWithinAsync(() => Run(service, operation, other));
        parked.Release.Set();
        await completing;

        served.ShouldBeTrue($"{operation} of a ticket in the completing ticket's lock slot waited for the next queued script's pod launch");
    }

    // ── what the narrower lock must still guarantee ──────────────────────────

    [Fact]
    public async Task Start_DuplicateWhileThePodIsBeingCreated_CreatesOnePod_AndReportsPending()
    {
        var service = NewService();
        var command = NewCommand();
        var parked = ParkPodCreate(PodOf(command));
        var starting = Task.Run(() => service.StartScript(command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();

        var duplicate = service.StartScript(command);
        parked.Release.Set();
        var first = await starting;

        duplicate.State.ShouldBeOneOf(ScriptStateV2.Pending, ScriptStateV2.Running);
        first.State.ShouldBe(ScriptStateV2.Running);
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public async Task Complete_ConcurrentDuplicates_OneCompletes_TheOtherReportsAlreadyCompleted_AndThePodIsDeletedOnce()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);
        var parked = ParkPodStatusRead(PodOf(command));

        var first = Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0)));
        var second = Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0)));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();
        parked.Release.Set();
        var outcomes = (await Task.WhenAll(first, second).WaitAsync(WaitLimit)).Select(r => r.Outcome).ToList();

        outcomes.Count(o => o == CompleteScriptOutcomeV2.Completed).ShouldBe(1);
        outcomes.Count(o => o == CompleteScriptOutcomeV2.AlreadyCompleted).ShouldBe(1);
        _ops.Verify(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task Complete_ReleasesTheMutex_AndTheQueuedScriptIsLaunchedBeforeCompleteReturns()
    {
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        service.StartScript(holder);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);

        var complete = await Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(holder.ScriptTicket, 0))).WaitAsync(WaitLimit);

        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        service.IsRunningScript(queued.ScriptTicket.TaskId).ShouldBeTrue();
        service.PendingScripts.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeFalse();
        service.MutexLocks.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeTrue();
        service.MutexLocks.ContainsKey(holder.ScriptTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_ReleasesTheMutex_AndTheQueuedScriptIsLaunchedBeforeCancelReturns()
    {
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        service.StartScript(holder);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);

        await Task.Run(() => service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0))).WaitAsync(WaitLimit);

        service.IsRunningScript(queued.ScriptTicket.TaskId).ShouldBeTrue();
        service.MutexLocks.ContainsKey(holder.ScriptTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_WhileThePodIsBeingCreated_WaitsForTheLaunch_ThenCancelsIt()
    {
        var service = NewService();
        var command = NewCommand();
        var parked = ParkPodCreate(PodOf(command));
        var starting = Task.Run(() => service.StartScript(command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();

        var cancelling = Task.Run(() => service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0)));
        await Task.Delay(300);
        parked.Release.Set();
        var cancel = await cancelling.WaitAsync(WaitLimit);
        await starting;

        cancel.State.ShouldBe(ScriptStateV2.Completed, "a Cancel that lands during the launch must still cancel the script, not be dropped");
        service.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeFalse();
        _ops.Verify(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task Complete_PodStartedAfterTheTerminationWait_IsNotCompletedUntilItHasBeenAwaited()
    {
        var service = NewService();
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        var waiter = NewCommandInOtherLockSlot(holder, ScriptIsolationLevel.FullIsolation, "lockscope-mx");
        service.StartScript(holder);
        service.StartScript(waiter).State.ShouldBe(ScriptStateV2.Pending);
        var parked = ParkPodCreate(PodOf(waiter));
        var launching = Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(holder.ScriptTicket, 0)));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();

        // The waiter's pod appears only after Complete(waiter) looked for something to await: it must not be finalised unawaited.
        service.LaunchStateReadHookForTests = () =>
        {
            service.LaunchStateReadHookForTests = null;
            parked.Release.Set();
            launching.Wait(WaitLimit).ShouldBeTrue();
        };
        var complete = service.CompleteScript(new CompleteScriptCommandV2(waiter.ScriptTicket, 0));
        await launching;

        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.NotYetComplete);
        service.IsRunningScript(waiter.ScriptTicket.TaskId).ShouldBeTrue("the pod was never waited on, so it must stay tracked for the next Complete");
        _ops.Verify(o => o.DeletePod(PodOf(waiter), "test-ns", It.IsAny<int?>()), Times.Never);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Park(ManualResetEventSlim Entered, ManualResetEventSlim Release);

    private ScriptPodService NewService() => new(_tentacleSettings, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings));

    private static string PodOf(StartScriptCommandV2 command) => $"squid-script-{command.ScriptTicket.TaskId[..12]}";

    private static StartScriptCommandV2 NewCommand(ScriptIsolationLevel isolation = ScriptIsolationLevel.NoIsolation, string? mutexName = null)
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = "echo hi", Isolation = isolation, IsolationMutexName = mutexName, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    private static StartScriptCommandV2 NewCommandInSameLockSlotAs(StartScriptCommandV2 other)
    {
        var stripes = new TicketLockStripes();

        while (true)
        {
            var candidate = NewCommand();

            if (ReferenceEquals(stripes.For(candidate.ScriptTicket.TaskId), stripes.For(other.ScriptTicket.TaskId))) return candidate;
        }
    }

    private static StartScriptCommandV2 NewCommandInOtherLockSlot(StartScriptCommandV2 other, ScriptIsolationLevel isolation, string mutexName)
    {
        var stripes = new TicketLockStripes();

        while (true)
        {
            var candidate = NewCommand(isolation, mutexName);

            if (!ReferenceEquals(stripes.For(candidate.ScriptTicket.TaskId), stripes.For(other.ScriptTicket.TaskId))) return candidate;
        }
    }

    private static object Run(ScriptPodService service, string operation, StartScriptCommandV2 command) => operation switch
    {
        "Start" => service.StartScript(command),
        "Complete" => service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0)),
        "Cancel" => service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    };

    private static async Task<bool> IsServedWithinAsync(Func<object> operation)
    {
        var task = Task.Run(operation);

        return await Task.WhenAny(task, Task.Delay(ServedWithin)).ConfigureAwait(false) == task;
    }

    private void SetupPodsSucceeded()
    {
        var state = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0 } };

        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Succeeded", ContainerStatuses = new List<V1ContainerStatus> { new() { Name = "script", State = state } } } });
    }

    // The next status read of this pod signals Entered, then parks until Release (what a slow-terminating pod looks like to Complete).
    private Park ParkPodStatusRead(string podName)
    {
        var park = new Park(new ManualResetEventSlim(), new ManualResetEventSlim());
        var state = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0 } };

        _ops.Setup(o => o.ReadPodStatus(podName, It.IsAny<string>())).Returns(() =>
        {
            park.Entered.Set();
            park.Release.Wait(WaitLimit);

            return new V1Pod { Status = new V1PodStatus { Phase = "Succeeded", ContainerStatuses = new List<V1ContainerStatus> { new() { Name = "script", State = state } } } };
        });

        return park;
    }

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

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
