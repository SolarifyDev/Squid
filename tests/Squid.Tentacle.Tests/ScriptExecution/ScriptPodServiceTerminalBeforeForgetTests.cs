using System;
using System.Collections.Generic;
using System.IO;
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
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> and <see cref="KubernetesPodMonitor"/> over a mocked Kubernetes API.
/// A ticket must always be visible in at least one of pending / tracked / terminal: a lock-free status read that lands between a transition's two
/// steps and finds it in none reads as lost or unknown, and the server fails the step with a misleading "may have run" message. Each test observes
/// the ticket FROM INSIDE the transition (a hook between publish and forget, or a Kubernetes call the transition makes) and asserts the real result.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceTerminalBeforeForgetTests : IDisposable
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly TentacleSettings _tentacleSettings;
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-terminalfirst-{Guid.NewGuid():N}");

    public ScriptPodServiceTerminalBeforeForgetTests()
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
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1Pod { Status = new V1PodStatus { Phase = "Pending" } });
    }

    // ── (a) the pod monitor gives up on a pod stuck in Pending ──────────────

    [Fact]
    public void FailPendingPods_TheTicketIsNeverInNeitherSetWhileItsPodIsDeletedAndItsResultPublished()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command).State.ShouldBe(ScriptStateV2.Running);
        var seen = new List<ScriptStatusResponseV2>();
        var monitor = NewMonitor(service);
        SetupStuckPendingPod(command);

        _ops.Setup(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>())).Callback(() => seen.Add(ReadV2(service, command)));
        service.BetweenPublishAndForgetHookForTests = () => seen.Add(ReadV2(service, command));

        monitor.FailPendingPods();

        seen.Count.ShouldBe(2, "the pod delete and the publish-then-forget step must both have been observed");
        seen.ShouldAllBe(s => s.State == ScriptStateV2.Running || s.State == ScriptStateV2.Completed, "a status read mid-transition must not see the ticket as lost or unknown");
        seen[1].State.ShouldBe(ScriptStateV2.Completed);
        seen[1].ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        ReadV2(service, command).ExitCode.ShouldBe(ScriptExitCodes.Timeout);
    }

    [Fact]
    public void FailPendingPods_TicketFinishedByAnotherCallerMeanwhile_PublishesNothing_AndKeepsTheMutex()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);
        var monitor = NewMonitor(service);
        SetupStuckPendingPod(command);

        _ops.Setup(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>())).Callback(() => service.ActiveScripts.TryRemove(command.ScriptTicket.TaskId, out _));

        monitor.FailPendingPods();

        service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.UnknownResult, "the monitor lost the race: it must not publish a result for a ticket someone else finished");
        service.MutexLocks.ContainsKey(command.ScriptTicket.TaskId).ShouldBeTrue("the mutex belongs to whoever finishes the ticket, not to the monitor that lost the race");
    }

    // ── (b) V2 Cancel of a live pod and of a queued script ──────────────────

    [Fact]
    public void CancelV2_LivePod_TheCanceledResultIsPublishedBeforeTheTicketStopsBeingTracked_AndBeforeTheTeardown()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);
        var seen = new List<ScriptStatusResponseV2>();

        _ops.Setup(o => o.DeletePod(PodOf(command), "test-ns", It.IsAny<int?>())).Callback(() => seen.Add(ReadV2(service, command)));
        service.BetweenPublishAndForgetHookForTests = () => seen.Add(ReadV2(service, command));

        service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));

        seen.Count.ShouldBe(2);
        seen.ShouldAllBe(s => s.State == ScriptStateV2.Completed && s.ExitCode == ScriptExitCodes.Canceled, "a status read during a Cancel must see Canceled, never lost or unknown");
        ReadV2(service, command).ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    [Fact]
    public void CancelV2_QueuedScript_TheCanceledResultIsPublishedBeforeTheTicketLeavesTheQueue()
    {
        var service = NewService();
        var (_, queued) = StartHolderAndQueued(service);
        ScriptStatusResponseV2? seen = null;

        service.BetweenPublishAndForgetHookForTests = () => seen = ReadV2(service, queued);

        service.CancelScript(new CancelScriptCommandV2(queued.ScriptTicket, 0));

        seen.ShouldNotBeNull();
        seen!.State.ShouldBeOneOf(new[] { ScriptStateV2.Pending, ScriptStateV2.Completed }, "mid-transition the ticket is still queued or already canceled, never lost or unknown");
        ReadV2(service, queued).ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    // ── (c) a queued script that waited past the isolation-mutex timeout ────

    [Fact]
    public void PendingTimeout_FoundByV1GetStatus_TheTimeoutResultIsPublishedBeforeTheTicketLeavesTheQueue()
    {
        _kubernetesSettings.IsolationMutexTimeoutMinutes = 0;
        var service = NewService();
        var (_, queued) = StartHolderAndQueued(service);
        ScriptStatusResponse? seen = null;

        service.BetweenPublishAndForgetHookForTests = () => seen = service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0));

        var status = service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0));

        seen.ShouldNotBeNull();
        seen!.ExitCode.ShouldBe(ScriptExitCodes.Timeout, "mid-transition the ticket must read as timed out, not unknown");
        status.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        service.PendingScripts.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeFalse();
    }

    [Fact]
    public void PendingTimeout_FoundWhenTheMutexIsReleased_TheTimeoutResultIsPublishedBeforeTheTicketLeavesTheQueue()
    {
        _kubernetesSettings.IsolationMutexTimeoutMinutes = 0;
        var service = NewService();
        var (holder, queued) = StartHolderAndQueued(service);
        ScriptStatusResponse? seen = null;
        var steps = 0;

        // The first publish-then-forget step is the holder's own Cancel; the second is the queued script's timeout, found when the mutex is released.
        service.BetweenPublishAndForgetHookForTests = () =>
        {
            if (++steps == 2) seen = service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0));
        };

        service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0));

        seen.ShouldNotBeNull("releasing the mutex must reach the queued script's timeout");
        seen!.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        service.GetStatus(new ScriptStatusRequest(queued.ScriptTicket, 0)).ExitCode.ShouldBe(ScriptExitCodes.Timeout);
    }

    // ── the publish-then-forget step is serialized with V2 Complete/Cancel/Start of the same ticket ──

    [Fact]
    public void FailPendingPods_ARacingV2CancelWaitsForThePublishThenForgetStep()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);
        var monitor = NewMonitor(service);
        SetupStuckPendingPod(command);
        System.Threading.Tasks.Task<ScriptStatusResponseV2>? completing = null;
        var completedInsideTheStep = false;

        service.BetweenPublishAndForgetHookForTests = () =>
        {
            completing = System.Threading.Tasks.Task.Run(() => service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0)));
            completedInsideTheStep = completing.Wait(TimeSpan.FromMilliseconds(300));
        };

        monitor.FailPendingPods();

        completedInsideTheStep.ShouldBeFalse("a V2 Cancel of the same ticket must wait for the transition instead of racing it");
        completing!.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
    }

    // ── lock-free readers scan in the direction a ticket moves ──────────────

    [Fact]
    public void GetStatusV1_TicketFinishesBetweenTheTrackedReadAndTheTerminalRead_ReportsItsResult()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);

        service.StatusSnapshotHookForTests = () =>
        {
            service.StatusSnapshotHookForTests = null;
            service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        };

        var status = service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));

        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled, "the ticket moved from tracked to terminal while it was being read");
    }

    [Fact]
    public void GetStatusV2_TicketFinishesBetweenTheTrackedReadAndTheTerminalRead_ReportsItsResult()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);

        service.KnownInMemoryReadHookForTests = () =>
        {
            service.KnownInMemoryReadHookForTests = null;
            service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        };

        var status = ReadV2(service, command);

        status.State.ShouldBe(ScriptStateV2.Completed);
        status.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private (StartScriptCommandV2 Holder, StartScriptCommandV2 Queued) StartHolderAndQueued(ScriptPodService service)
    {
        var holder = NewCommand(ScriptIsolationLevel.FullIsolation, "terminalfirst-mx");
        var queued = NewCommand(ScriptIsolationLevel.FullIsolation, "terminalfirst-mx");
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);

        return (holder, queued);
    }

    private static ScriptStatusResponseV2 ReadV2(ScriptPodService service, StartScriptCommandV2 command) => service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));

    private void SetupStuckPendingPod(StartScriptCommandV2 command)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = PodOf(command), CreationTimestamp = DateTime.UtcNow.AddMinutes(-10), Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = command.ScriptTicket.TaskId } },
            Status = new V1PodStatus { Phase = "Pending" }
        };

        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>())).Returns(new V1PodList { Items = new List<V1Pod> { pod } });
    }

    private ScriptPodService NewService() => new(_tentacleSettings, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings));

    private KubernetesPodMonitor NewMonitor(ScriptPodService service) => new(new KubernetesPodManager(_ops.Object, _kubernetesSettings), service, _tentacleSettings, _kubernetesSettings);

    private static string PodOf(StartScriptCommandV2 command) => $"squid-script-{command.ScriptTicket.TaskId[..12]}";

    private static StartScriptCommandV2 NewCommand(ScriptIsolationLevel isolation = ScriptIsolationLevel.NoIsolation, string? mutexName = null)
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = "echo hi", Isolation = isolation, IsolationMutexName = mutexName, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
