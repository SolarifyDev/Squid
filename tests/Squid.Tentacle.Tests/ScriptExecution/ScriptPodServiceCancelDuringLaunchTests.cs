using System;
using System.Collections.Generic;
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
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> over a mocked Kubernetes API. A Cancel is sent once and its answer
/// is ignored by the server, so a Cancel that meets a ticket in the middle of its launch (dequeued from the pending queue, or a Start whose
/// pod creation outlasts the settle wait) must still end with no pod running: the launcher honours a recorded cancel request.
/// Hooks make each interleaving deterministic; every wait is bounded.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceCancelDuringLaunchTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-cancellaunch-{Guid.NewGuid():N}");

    public ScriptPodServiceCancelDuringLaunchTests()
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

    [Fact]
    public async Task Cancel_QueuedTicketDequeuedWhileCancelWaitsForTheLock_CancelsThePodOnceItIsCreated()
    {
        var service = NewService();
        var (holder, queued) = StartHolderAndQueued(service);
        var parked = ParkPodCreate(PodOf(queued));
        Task? releasing = null;

        service.BeforeCancelLockHookForTests = () =>
        {
            service.BeforeCancelLockHookForTests = null;
            releasing = Task.Run(() => service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0)));
            parked.Entered.Wait(WaitLimit).ShouldBeTrue("the holder's release must dequeue the queued script and start creating its pod");
        };

        var cancel = service.CancelScript(new CancelScriptCommandV2(queued.ScriptTicket, 0));
        AssertLauncherStillOwnsTheMutex(service, queued);
        parked.Release.Set();
        await releasing!.WaitAsync(WaitLimit);

        AssertCancelledAndFullyReleased(service, queued, cancel);
    }

    [Fact]
    public async Task Cancel_LaunchOutlastsTheSettleWait_CancelsThePodOnceItIsCreated()
    {
        var service = NewService();
        service.LaunchSettleTimeoutForTests = TimeSpan.FromMilliseconds(50);
        var command = NewCommand();
        var parked = ParkPodCreate(PodOf(command));
        var starting = Task.Run(() => service.StartScript(command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();

        var cancel = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        AssertLauncherStillOwnsTheMutex(service, command);
        parked.Release.Set();
        await starting.WaitAsync(WaitLimit);

        AssertCancelledAndFullyReleased(service, command, cancel);
    }

    [Fact]
    public async Task Cancel_QueuedTicketDequeuedBetweenTheQueueReadAndTheRemove_CancelsThePodOnceItIsCreated()
    {
        var service = NewService();
        var (holder, queued) = StartHolderAndQueued(service);
        var parked = ParkPodCreate(PodOf(queued));
        Task? releasing = null;

        service.BeforeCancelCoreHookForTests = () =>
        {
            service.BeforeCancelCoreHookForTests = null;
            releasing = Task.Run(() => service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0)));
            parked.Entered.Wait(WaitLimit).ShouldBeTrue("the holder's release must dequeue the queued script and start creating its pod");
        };

        var cancel = service.CancelScript(new CancelScriptCommandV2(queued.ScriptTicket, 0));
        AssertLauncherStillOwnsTheMutex(service, queued);
        parked.Release.Set();
        await releasing!.WaitAsync(WaitLimit);

        AssertCancelledAndFullyReleased(service, queued, cancel);
    }

    [Fact]
    public async Task CancelV1_QueuedTicketDequeuedBetweenTheQueueReadAndTheRemove_LeavesTheMutexToTheLauncher_ThenCancelsThePod()
    {
        var service = NewService();
        var (holder, queued) = StartHolderAndQueued(service);
        var parked = ParkPodCreate(PodOf(queued));
        Task? releasing = null;

        service.BeforeCancelCoreHookForTests = () =>
        {
            service.BeforeCancelCoreHookForTests = null;
            releasing = Task.Run(() => service.CancelScript(new CancelScriptCommand(holder.ScriptTicket, 0)));
            parked.Entered.Wait(WaitLimit).ShouldBeTrue("the holder's release must dequeue the queued script and start creating its pod");
        };

        service.CancelScript(new CancelScriptCommand(queued.ScriptTicket, 0));
        AssertLauncherStillOwnsTheMutex(service, queued);
        parked.Release.Set();
        await releasing!.WaitAsync(WaitLimit);

        service.IsRunningScript(queued.ScriptTicket.TaskId).ShouldBeFalse();
        service.MutexLocks.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeFalse();
        _ops.Verify(o => o.DeletePod(PodOf(queued), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_DuringLaunchThatFails_LeavesNoStaleCancelRequest_AndReleasesTheMutex()
    {
        var service = NewService();
        service.LaunchSettleTimeoutForTests = TimeSpan.FromMilliseconds(50);
        var command = NewCommand();
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            entered.Set();
            gate.Wait(WaitLimit);

            throw new InvalidOperationException("boom");
        });

        var starting = Task.Run(() => service.StartScript(command));
        entered.Wait(WaitLimit).ShouldBeTrue();

        service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        gate.Set();
        await starting.WaitAsync(WaitLimit);

        service.MutexLocks.ContainsKey(command.ScriptTicket.TaskId).ShouldBeFalse();
        service.HasCancelRequestForTests(command.ScriptTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_LaunchEndsBetweenTheLaunchingReadAndTheRequest_LeavesNoStaleCancelRequest()
    {
        var service = NewService();
        service.LaunchSettleTimeoutForTests = TimeSpan.FromMilliseconds(50);
        var command = NewCommand();
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            entered.Set();
            gate.Wait(WaitLimit);

            throw new InvalidOperationException("boom");
        });

        var starting = Task.Run(() => service.StartScript(command));
        entered.Wait(WaitLimit).ShouldBeTrue();

        service.BeforeCancelRequestRecordedHookForTests = () =>
        {
            service.BeforeCancelRequestRecordedHookForTests = null;
            gate.Set();
            starting.Wait(WaitLimit).ShouldBeTrue("the failed launch must finish while the Cancel is between its two reads");
        };

        service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));

        service.HasCancelRequestForTests(command.ScriptTicket.TaskId).ShouldBeFalse("a request recorded after the launch ended would never be consumed");
    }

    [Fact]
    public async Task Cancel_PodBecomesTrackedBetweenTheLaunchingReadAndTheRequest_CancelsThatPodItself()
    {
        var service = NewService();
        service.LaunchSettleTimeoutForTests = TimeSpan.FromMilliseconds(50);
        var command = NewCommand();
        var parked = ParkPodCreate(PodOf(command));
        var starting = Task.Run(() => service.StartScript(command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();

        service.BeforeCancelRequestRecordedHookForTests = () =>
        {
            service.BeforeCancelRequestRecordedHookForTests = null;
            parked.Release.Set();
            starting.Wait(WaitLimit).ShouldBeTrue("the launch must finish, its cancel check already past, before the Cancel records its request");
        };

        var cancel = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        await starting.WaitAsync(WaitLimit);

        AssertCancelledAndFullyReleased(service, command, cancel);
    }

    [Fact]
    public async Task Cancel_RecordsItsRequestAfterTheLauncherCheckedButBeforeTheLauncherFinished_CancelsThePodItself()
    {
        var service = NewService();
        service.LaunchSettleTimeoutForTests = TimeSpan.FromMilliseconds(50);
        var command = NewCommand();
        var parked = ParkPodCreate(PodOf(command));
        var starting = Task.Run(() => service.StartScript(command));
        parked.Entered.Wait(WaitLimit).ShouldBeTrue();
        using var launcherChecked = new ManualResetEventSlim();
        using var releaseLauncher = new ManualResetEventSlim();

        // The launcher has tracked the pod and found no cancel request, then waits; the Cancel records its request only now, and the launcher
        // finishes (everything it does after its check) before the Cancel arbitrates ownership of that request.
        service.AfterHonourCheckHookForTests = () => { service.AfterHonourCheckHookForTests = null; launcherChecked.Set(); releaseLauncher.Wait(WaitLimit); };
        service.BeforeCancelRequestRecordedHookForTests = () =>
        {
            service.BeforeCancelRequestRecordedHookForTests = null;
            parked.Release.Set();
            launcherChecked.Wait(WaitLimit).ShouldBeTrue("the launcher must reach its cancel check");
        };
        service.AfterCancelRequestRecordedHookForTests = () =>
        {
            service.AfterCancelRequestRecordedHookForTests = null;
            releaseLauncher.Set();
            starting.Wait(WaitLimit).ShouldBeTrue("the launch must finish between the Cancel's record and its arbitration");
        };

        var cancel = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));

        AssertCancelledAndFullyReleased(service, command, cancel);
    }

    [Fact]
    public async Task Cancel_DequeuedLaunchThatFails_LeavesNoStaleCancelRequest()
    {
        var service = NewService();
        var (holder, queued) = StartHolderAndQueued(service);
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        Task? releasing = null;

        _ops.Setup(o => o.CreatePod(It.Is<V1Pod>(p => p.Metadata.Name == PodOf(queued)), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            entered.Set();
            gate.Wait(WaitLimit);

            throw new InvalidOperationException("boom");
        });

        service.BeforeCancelCoreHookForTests = () =>
        {
            service.BeforeCancelCoreHookForTests = null;
            releasing = Task.Run(() => service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0)));
            entered.Wait(WaitLimit).ShouldBeTrue();
        };

        service.CancelScript(new CancelScriptCommandV2(queued.ScriptTicket, 0));
        gate.Set();
        await releasing!.WaitAsync(WaitLimit);

        service.HasCancelRequestForTests(queued.ScriptTicket.TaskId).ShouldBeFalse();
        service.MutexLocks.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancel_AfterTheLaunchFinished_CancelsTheTrackedPodDirectly()
    {
        var service = NewService();
        var command = NewCommand();
        await Task.Run(() => service.StartScript(command)).WaitAsync(WaitLimit);

        var cancel = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));

        cancel.State.ShouldBe(ScriptStateV2.Completed);
        service.IsRunningScript(command.ScriptTicket.TaskId).ShouldBeFalse();
        service.HasCancelRequestForTests(command.ScriptTicket.TaskId).ShouldBeFalse();
        _ops.Verify(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    private static void AssertLauncherStillOwnsTheMutex(ScriptPodService service, StartScriptCommandV2 command)
        => service.MutexLocks.ContainsKey(command.ScriptTicket.TaskId).ShouldBeTrue("the Cancel only recorded a request: the launcher still holds the isolation mutex until its pod is torn down");

    private void AssertCancelledAndFullyReleased(ScriptPodService service, StartScriptCommandV2 command, ScriptStatusResponseV2 cancel)
    {
        var ticketId = command.ScriptTicket.TaskId;
        var status = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));

        status.State.ShouldBe(ScriptStateV2.Completed, $"a Cancel that met the launch must still end the script (the Cancel itself answered {cancel.State})");
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        service.IsRunningScript(ticketId).ShouldBeFalse();
        service.MutexLocks.ContainsKey(ticketId).ShouldBeFalse("the cancelled launch must release the isolation mutex");
        service.HasCancelRequestForTests(ticketId).ShouldBeFalse();
        _ops.Verify(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    private (StartScriptCommandV2 Holder, StartScriptCommandV2 Queued) StartHolderAndQueued(ScriptPodService service)
    {
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "cancellaunch-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "cancellaunch-mx");
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);

        return (holder, queued);
    }

    private sealed record Park(ManualResetEventSlim Entered, ManualResetEventSlim Release);

    private ScriptPodService NewService() => new(_tentacleSettings, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings));

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

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
