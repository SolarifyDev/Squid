using System;
using System.Collections.Generic;
using System.IO;
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
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> over a mocked Kubernetes API. A test-only hook that runs
/// between the service's reads of where a ticket is pins each interleaving deterministically: a Start (or a queued script leaving
/// the pending queue) that finishes between two reads must never make a live pod look unknown or lost, because UnknownTicket
/// makes the server re-dispatch (the script runs twice) and a lost pod is reported without its result.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceStatusRaceParityTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-race-{Guid.NewGuid():N}");

    public ScriptPodServiceStatusRaceParityTests()
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
    }

    // ── a Start that finishes between the service's reads ────────────────────

    [Fact]
    public async Task GetStatus_StartFinishesBetweenTheReads_ReportsPendingOrRunning_NeverLostOrUnknown()
    {
        var service = NewService();
        var gate = BlockCreatePod();
        var command = NewCommand("echo hi");
        var start = Task.Run(() => service.StartScript(command));
        gate.Entered.Wait(WaitLimit).ShouldBeTrue();

        ArmOnce(service, () => { gate.Release.Set(); start.Wait(WaitLimit).ShouldBeTrue(); });
        var status = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));
        await start;

        service.LaunchStateReadHookForTests.ShouldBeNull("the interleaving hook must run inside GetStatus");
        status.State.ShouldBeOneOf(ScriptStateV2.Pending, ScriptStateV2.Running);
    }

    [Fact]
    public async Task GetStatus_QueuedScriptLeavesThePendingQueueBetweenTheReads_ReportsPending_NeverLostOrUnknown()
    {
        var service = NewService();
        var (holder, waiter) = StartHolderAndQueuedWaiter(service);
        var gate = BlockCreatePod();
        SetupContainer("Succeeded", 0);
        Task<CompleteScriptResponseV2>? completing = null;

        ArmOnce(service, () => completing = CompleteInBackground(service, holder, gate));
        var status = service.GetStatus(new ScriptStatusRequestV2(waiter.ScriptTicket, 0));
        gate.Release.Set();
        await completing!;

        service.LaunchStateReadHookForTests.ShouldBeNull("the interleaving hook must run inside GetStatus");
        status.State.ShouldBe(ScriptStateV2.Pending, "between leaving the pending queue and the pod being tracked the script is launching, not lost");
    }

    [Fact]
    public async Task Complete_QueuedScriptLaunchFinishesBetweenTheReads_NeverReleasesItAsLost()
    {
        var service = NewService();
        var (holder, waiter) = StartHolderAndQueuedWaiter(service);
        var gate = BlockCreatePod();
        SetupContainer("Succeeded", 0);
        var completing = CompleteInBackground(service, holder, gate);

        ArmOnce(service, () => FinishLaunch(gate, completing));
        var complete = service.CompleteScript(new CompleteScriptCommandV2(waiter.ScriptTicket, 0));
        await completing;

        service.LaunchStateReadHookForTests.ShouldBeNull("the interleaving hook must run inside CompleteScript");
        complete.Outcome.ShouldBeOneOf(CompleteScriptOutcomeV2.NotYetComplete, CompleteScriptOutcomeV2.Completed);
        (complete.FinalStatus?.State).ShouldNotBe(ScriptStateV2.ResultLost);
    }

    [Fact]
    public async Task Cancel_QueuedScriptLaunchFinishesBetweenTheReads_StillCancelsTheScript()
    {
        var service = NewService();
        var (holder, waiter) = StartHolderAndQueuedWaiter(service);
        var gate = BlockCreatePod();
        SetupContainer("Succeeded", 0);
        var completing = CompleteInBackground(service, holder, gate);

        ArmOnce(service, () => FinishLaunch(gate, completing));
        var cancel = service.CancelScript(new CancelScriptCommandV2(waiter.ScriptTicket, 0));
        await completing;

        service.LaunchStateReadHookForTests.ShouldBeNull("the interleaving hook must run inside CancelScript");
        cancel.State.ShouldNotBe(ScriptStateV2.ResultLost);
        service.IsRunningScript(waiter.ScriptTicket.TaskId).ShouldBeFalse("a Cancel that lands as the pod is tracked must cancel it, not be answered as if the ticket were gone");
    }

    [Fact]
    public async Task Start_DuplicateWhileAQueuedScriptIsBeingLaunched_ReportsPending_NotLost()
    {
        var service = NewService();
        var (holder, waiter) = StartHolderAndQueuedWaiter(service);
        var gate = BlockCreatePod();
        SetupContainer("Succeeded", 0);
        var completing = CompleteInBackground(service, holder, gate);

        var duplicate = service.StartScript(waiter);
        gate.Release.Set();
        await completing;

        duplicate.State.ShouldBeOneOf(ScriptStateV2.Pending, ScriptStateV2.Running);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Gate(ManualResetEventSlim Entered, ManualResetEventSlim Release);

    private ScriptPodService NewService() => new(_tentacleSettings, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings));

    // The next CreatePod signals Entered, then parks until Release.
    private Gate BlockCreatePod()
    {
        var gate = new Gate(new ManualResetEventSlim(), new ManualResetEventSlim());

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            gate.Entered.Set();
            gate.Release.Wait(WaitLimit);

            return pod;
        });

        return gate;
    }

    private static void ArmOnce(ScriptPodService service, Action hook)
        => service.LaunchStateReadHookForTests = () =>
        {
            service.LaunchStateReadHookForTests = null;
            hook();
        };

    private static void FinishLaunch(Gate gate, Task launching)
    {
        gate.Release.Set();
        launching.Wait(WaitLimit).ShouldBeTrue();
    }

    // Completing the holder frees the isolation mutex, so the waiter leaves the pending queue and its (parked) pod launch begins.
    private static Task<CompleteScriptResponseV2> CompleteInBackground(ScriptPodService service, StartScriptCommandV2 holder, Gate gate)
    {
        var completing = Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(holder.ScriptTicket, 0)));

        if (!gate.Entered.Wait(WaitLimit)) throw new TimeoutException("the queued script's pod launch never began");

        return completing;
    }

    private (StartScriptCommandV2 Holder, StartScriptCommandV2 Waiter) StartHolderAndQueuedWaiter(ScriptPodService service)
    {
        var holder = NewCommand("echo first", ScriptIsolationLevel.FullIsolation, "pod-race-mx");
        var waiter = NewCommandInOtherLockSlot(holder, "echo second", ScriptIsolationLevel.FullIsolation, "pod-race-mx");

        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(waiter).State.ShouldBe(ScriptStateV2.Pending);

        return (holder, waiter);
    }

    // Different ticket-lock stripes, so a test thread holding one ticket's lock never waits on the other's.
    private static StartScriptCommandV2 NewCommandInOtherLockSlot(StartScriptCommandV2 other, string body, ScriptIsolationLevel isolation, string mutexName)
    {
        var stripes = new TicketLockStripes();

        while (true)
        {
            var candidate = NewCommand(body, isolation, mutexName);

            if (!ReferenceEquals(stripes.For(candidate.ScriptTicket.TaskId), stripes.For(other.ScriptTicket.TaskId))) return candidate;
        }
    }

    private static StartScriptCommandV2 NewCommand(string body, ScriptIsolationLevel isolation = ScriptIsolationLevel.NoIsolation, string? mutexName = null)
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = body, Isolation = isolation, IsolationMutexName = mutexName, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    private void SetupContainer(string phase, int exitCode)
    {
        var state = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = exitCode } };

        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase, ContainerStatuses = new List<V1ContainerStatus> { new() { Name = "script", State = state } } } });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
