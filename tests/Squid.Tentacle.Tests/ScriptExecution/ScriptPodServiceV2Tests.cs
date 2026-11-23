using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> (real workspace files, state store, tombstone
/// files, pending queue, isolation mutex) over a mocked Kubernetes API returning realistic pod objects, exactly as
/// <see cref="ScriptPodServiceTests"/> does. A restart is a new service instance over the same workspace volume.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceV2Tests : IDisposable
{
    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-v2-pod-{Guid.NewGuid():N}");

    public ScriptPodServiceV2Tests()
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
        SetupPodLogs("");
    }

    [Fact]
    public void Start_NewScript_ReportsRunning_AndRepeatNeverCreatesASecondPod()
    {
        var service = NewService();
        var command = NewCommand("echo hi");

        var first = service.StartScript(command);
        var repeat = service.StartScript(command);

        first.State.ShouldBe(ScriptStateV2.Running);
        repeat.State.ShouldBe(ScriptStateV2.Running);
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void GetStatus_RunningThenCompleted_ReportsRealContainerExitCode_MinusOneIncluded()
    {
        var service = NewService();
        var command = NewCommand("exit 1");
        service.StartScript(command);

        SetupContainer("Running", null);
        var running = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));

        SetupContainer("Failed", -1);
        var done = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));

        running.State.ShouldBe(ScriptStateV2.Running);
        done.State.ShouldBe(ScriptStateV2.Completed);
        done.ExitCode.ShouldBe(-1, "a real -1 container exit is a Completed result, never an unknown ticket");
    }

    [Fact]
    public void GetStatus_QueuedBehindIsolationMutex_ReportsPending()
    {
        var service = NewService();
        service.StartScript(NewCommand("echo first", ScriptIsolationLevel.FullIsolation, "mx"));
        var queued = NewCommand("echo second", ScriptIsolationLevel.FullIsolation, "mx");

        var start = service.StartScript(queued);
        var status = service.GetStatus(new ScriptStatusRequestV2(queued.ScriptTicket, 0));

        start.State.ShouldBe(ScriptStateV2.Pending);
        status.State.ShouldBe(ScriptStateV2.Pending);
    }

    [Fact]
    public void UnknownTicket_GetStatusCancelComplete_AreExplicit()
    {
        var service = NewService();
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));

        service.GetStatus(new ScriptStatusRequestV2(ticket, 0)).State.ShouldBe(ScriptStateV2.UnknownTicket);
        service.CancelScript(new CancelScriptCommandV2(ticket, 0)).State.ShouldBe(ScriptStateV2.UnknownTicket);
        service.CompleteScript(new CompleteScriptCommandV2(ticket, 0)).Outcome.ShouldBe(CompleteScriptOutcomeV2.UnknownTicket);
    }

    [Fact]
    public void Complete_QueuedBehindIsolationMutex_ReturnsNotYetComplete_AndLeavesItQueued()
    {
        var service = NewService();
        service.StartScript(NewCommand("echo first", ScriptIsolationLevel.FullIsolation, "mx"));
        var queued = NewCommand("echo second", ScriptIsolationLevel.FullIsolation, "mx");
        service.StartScript(queued);

        var response = service.CompleteScript(new CompleteScriptCommandV2(queued.ScriptTicket, 0));

        response.Outcome.ShouldBe(CompleteScriptOutcomeV2.NotYetComplete);
        response.FinalStatus!.State.ShouldBe(ScriptStateV2.Pending);
        service.PendingScripts.ContainsKey(queued.ScriptTicket.TaskId).ShouldBeTrue();
    }

    [Fact]
    public void CompleteThenComplete_SecondIsAlreadyCompleted_AlsoAcrossRestart()
    {
        var service = NewService();
        var command = NewCommand("echo done");
        service.StartScript(command);
        SetupContainer("Succeeded", 0);

        var first = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var second = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var afterRestart = NewService().CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));

        first.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        first.FinalStatus!.State.ShouldBe(ScriptStateV2.Completed);
        first.FinalStatus.ExitCode.ShouldBe(0);
        second.Outcome.ShouldBe(CompleteScriptOutcomeV2.AlreadyCompleted);
        afterRestart.Outcome.ShouldBe(CompleteScriptOutcomeV2.AlreadyCompleted);
        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public void RestartWithRecordedExitCode_ReportsCompletedWithThatCode_ThenReleases()
    {
        var ticket = SaveState(new ScriptState { Progress = ScriptProgress.Complete, ExitCode = 12, NextLogSequence = 4 });
        var restarted = NewService();

        var status = restarted.GetStatus(new ScriptStatusRequestV2(ticket, 0));
        var complete = restarted.CompleteScript(new CompleteScriptCommandV2(ticket, 0));

        status.State.ShouldBe(ScriptStateV2.Completed);
        status.ExitCode.ShouldBe(12);
        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.ExitCode.ShouldBe(12);
        Directory.Exists(Path.Combine(_workspace, ticket.TaskId)).ShouldBeFalse();
    }

    [Fact]
    public void RestartWithRunningStateAndNoPodTracked_ReportsResultLost_AndStartDoesNotRelaunch()
    {
        var ticket = SaveState(new ScriptState { Progress = ScriptProgress.Running });
        var restarted = NewService();

        var status = restarted.GetStatus(new ScriptStatusRequestV2(ticket, 0));
        var start = restarted.StartScript(new StartScriptCommandV2 { ScriptTicket = ticket, ScriptBody = "echo again" });
        var complete = restarted.CompleteScript(new CompleteScriptCommandV2(ticket, 0));

        status.State.ShouldBe(ScriptStateV2.ResultLost);
        start.State.ShouldBe(ScriptStateV2.ResultLost);
        complete.FinalStatus!.State.ShouldBe(ScriptStateV2.ResultLost);
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Cancel_RunningScript_IsIdempotent_AndCompleteStillReportsCanceled()
    {
        var service = NewService();
        var command = NewCommand("sleep 999");
        service.StartScript(command);

        var first = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        var second = service.CancelScript(new CancelScriptCommandV2(command.ScriptTicket, 0));
        var complete = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));

        first.State.ShouldBe(ScriptStateV2.Completed);
        first.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        second.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.ExitCode.ShouldBe(ScriptExitCodes.Canceled);
        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public void Start_LaunchFailure_IsAnAgentAssertedCompletedResult()
    {
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Throws(new InvalidOperationException("quota exceeded"));
        var service = NewService();
        var command = NewCommand("echo x");

        var status = service.StartScript(command);

        status.State.ShouldBe(ScriptStateV2.Completed);
        status.ExitCode.ShouldBe(ScriptExitCodes.Fatal);
        service.StartScript(command).ExitCode.ShouldBe(ScriptExitCodes.Fatal, "a repeated Start returns the same result without another launch attempt");
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void MonitorOrphanSweep_SkipsTombstoneDirectory()
    {
        var service = NewService();
        var command = NewCommand("echo done");
        service.StartScript(command);
        SetupContainer("Succeeded", 0);
        service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var tombstones = Path.Combine(_workspace, ScriptTombstoneStore.WorkspaceDirectoryName);
        Directory.SetLastWriteTimeUtc(tombstones, DateTime.UtcNow - TimeSpan.FromDays(30));

        var podManager = new KubernetesPodManager(_ops.Object, _kubernetesSettings);
        var monitor = new KubernetesPodMonitor(podManager, service, _tentacleSettings, _kubernetesSettings);
        InvokeOrphanSweep(monitor);

        Directory.Exists(tombstones).ShouldBeTrue("the pod monitor's age-based orphan sweep must not delete the completion tombstones");
    }

    private static void InvokeOrphanSweep(KubernetesPodMonitor monitor)
    {
        var method = typeof(KubernetesPodMonitor).GetMethod("CleanupOrphanedWorkspaces", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        method.Invoke(monitor, null);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ScriptPodService NewService()
    {
        var podManager = new KubernetesPodManager(_ops.Object, _kubernetesSettings);

        return new ScriptPodService(_tentacleSettings, _kubernetesSettings, podManager);
    }

    private static StartScriptCommandV2 NewCommand(string body, ScriptIsolationLevel isolation = ScriptIsolationLevel.NoIsolation, string? mutexName = null)
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = body, Isolation = isolation, IsolationMutexName = mutexName, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    private ScriptTicket SaveState(ScriptState state)
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        state.TicketId = ticket.TaskId;
        state.CreatedAt = DateTimeOffset.UtcNow;
        new ScriptStateStore(Path.Combine(_workspace, ticket.TaskId)).Save(state);

        return ticket;
    }

    private void SetupContainer(string phase, int? exitCode)
    {
        var state = exitCode is { } code
            ? new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = code } }
            : new V1ContainerState { Running = new V1ContainerStateRunning() };

        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase, ContainerStatuses = new List<V1ContainerStatus> { new() { Name = "script", State = state } } } });
    }

    private void SetupPodLogs(string logs)
    {
        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
            .Returns(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logs)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
