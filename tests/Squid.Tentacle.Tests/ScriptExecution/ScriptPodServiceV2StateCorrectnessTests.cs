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
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.ScriptExecution.V2;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/>, <see cref="ScriptRecoveryService"/>, state store
/// and tombstone files over a mocked Kubernetes API (same shape as <see cref="ScriptPodServiceV2Tests"/>). The recovery
/// tests drive <see cref="ScriptRecoveryService"/> itself rather than seeding the service's in-memory state.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceV2StateCorrectnessTests : IDisposable
{
    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-v2-pod-state-{Guid.NewGuid():N}");

    public ScriptPodServiceV2StateCorrectnessTests()
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

    // ── restart recovery goes through ScriptRecoveryService ──────────────────

    [Fact]
    public void RecoveredScriptWhosePodIsGone_ReportsResultLost_BeforeAndAfterEviction_AndCompleteRecordsLost()
    {
        var ticket = SeedRunningScriptState("squid-script-gone");
        SetupPodNotFound();
        var service = NewService();

        new ScriptRecoveryService().RecoverScripts(_workspace, service, PodManager(), new ScriptIsolationMutex());

        var request = new ScriptStatusRequestV2(ticket, 0);
        var beforeEviction = service.GetStatus(request);
        service.EvictStaleTerminalResults(TimeSpan.Zero);
        var afterEviction = service.GetStatus(request);
        var v1 = NewService().GetStatus(new ScriptStatusRequest(ticket, 0));

        beforeEviction.State.ShouldBe(ScriptStateV2.ResultLost, "a recovery-injected UnknownResult is not a real exit code");
        afterEviction.State.ShouldBe(ScriptStateV2.ResultLost, "the same ticket must not change its answer when the in-memory entry is evicted");
        v1.ExitCode.ShouldBe(Squid.Message.Constants.ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void RecoveredLostScript_CompleteReleasesWithResultLost_AndLaterStatusStaysLost()
    {
        var ticket = SeedRunningScriptState("squid-script-gone-2");
        SetupPodNotFound();
        var service = NewService();
        new ScriptRecoveryService().RecoverScripts(_workspace, service, PodManager(), new ScriptIsolationMutex());

        var complete = service.CompleteScript(new CompleteScriptCommandV2(ticket, 0));
        var after = service.GetStatus(new ScriptStatusRequestV2(ticket, 0));
        var again = service.CompleteScript(new CompleteScriptCommandV2(ticket, 0));

        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.State.ShouldBe(ScriptStateV2.ResultLost);
        after.State.ShouldBe(ScriptStateV2.ResultLost);
        again.Outcome.ShouldBe(CompleteScriptOutcomeV2.AlreadyCompleted);
    }

    [Fact]
    public void RecoveredCompletedPod_StillReportsTheRealExitCode_NotLost()
    {
        var ticket = SeedRunningScriptState("squid-script-finished");
        SetupContainer("Succeeded", 3);
        var service = NewService();

        new ScriptRecoveryService().RecoverScripts(_workspace, service, PodManager(), new ScriptIsolationMutex());
        var status = service.GetStatus(new ScriptStatusRequestV2(ticket, 0));

        status.State.ShouldBe(ScriptStateV2.Completed);
        status.ExitCode.ShouldBe(3);
    }

    // ── a launching pod is Pending, never lost / unknown ─────────────────────

    [Fact]
    public async Task GetStatus_WhileStartIsLaunchingThePod_ReportsPending()
    {
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));

            return pod;
        });
        var service = NewService();
        var command = NewCommand("echo hi");

        var start = Task.Run(() => service.StartScript(command));
        entered.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();

        var status = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));
        release.Set();
        await start;

        status.State.ShouldBe(ScriptStateV2.Pending, "the persisted state already says Starting; reading it as lost or unknown makes the server re-dispatch");
    }

    [Fact]
    public async Task GetStatus_WhileAQueuedScriptIsBeingLaunchedAfterTheMutexFrees_ReportsPending()
    {
        var service = NewService();
        var holder = NewCommand("echo first", ScriptIsolationLevel.FullIsolation, "pod-queue-mx");
        var queued = NewCommand("echo second", ScriptIsolationLevel.FullIsolation, "pod-queue-mx");
        service.StartScript(holder);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);

        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>())).Returns((V1Pod pod, string ns) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));

            return pod;
        });
        SetupContainer("Succeeded", 0);

        var completing = Task.Run(() => service.CompleteScript(new CompleteScriptCommandV2(holder.ScriptTicket, 0)));
        entered.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();

        var status = service.GetStatus(new ScriptStatusRequestV2(queued.ScriptTicket, 0));
        release.Set();
        await completing;

        status.State.ShouldBe(ScriptStateV2.Pending);
    }

    // ── a finished ticket is never launched again ────────────────────────────

    [Fact]
    public void Start_AfterCompleteReleasedTheTicket_ReportsTheRecordedResult_AndCreatesNoSecondPod()
    {
        var service = NewService();
        var command = NewCommand("exit 6");
        service.StartScript(command);
        SetupContainer("Failed", 6);
        service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0)).Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);

        var duplicate = service.StartScript(command);
        var afterRestart = NewService().StartScript(command);

        duplicate.State.ShouldBe(ScriptStateV2.Completed);
        duplicate.ExitCode.ShouldBe(6);
        afterRestart.State.ShouldBe(ScriptStateV2.Completed);
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Once);
    }

    // ── a tombstone that cannot be written must not release the only record ──

    [Fact]
    public void Complete_TombstoneCannotBeWritten_KeepsTheStateFile_SoARestartStillSeesTheResult()
    {
        var service = NewService();
        service.Tombstones = UnwritableTombstones();
        var command = NewCommand("exit 7");
        service.StartScript(command);
        SetupContainer("Failed", 7);

        var complete = service.CompleteScript(new CompleteScriptCommandV2(command.ScriptTicket, 0));
        var restarted = NewService();
        restarted.Tombstones = UnwritableTombstones();
        var afterRestart = restarted.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));

        complete.Outcome.ShouldBe(CompleteScriptOutcomeV2.Completed);
        complete.FinalStatus!.ExitCode.ShouldBe(7);
        afterRestart.State.ShouldBe(ScriptStateV2.Completed, "releasing the state without a tombstone turns a finished script into an unknown one after a crash");
        afterRestart.ExitCode.ShouldBe(7);
        Directory.GetFiles(Path.Combine(_workspace, command.ScriptTicket.TaskId)).ShouldAllBe(f => Path.GetFileName(f).StartsWith("scriptstate.json"), "only the state file is kept; the script and its variables are scrubbed");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ScriptPodService NewService() => new(_tentacleSettings, _kubernetesSettings, PodManager());

    private KubernetesPodManager PodManager() => new(_ops.Object, _kubernetesSettings);

    private ScriptTombstoneStore UnwritableTombstones()
    {
        var blocker = Path.Combine(_workspace, "tombstone-blocker");
        if (!File.Exists(blocker)) File.WriteAllText(blocker, "x");

        return new ScriptTombstoneStore(Path.Combine(blocker, "tombstones"));
    }

    private static StartScriptCommandV2 NewCommand(string body, ScriptIsolationLevel isolation = ScriptIsolationLevel.NoIsolation, string? mutexName = null)
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = body, Isolation = isolation, IsolationMutexName = mutexName, ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    // What a real run leaves behind: the pod state file recovery reads plus the generic persisted state written at launch.
    private ScriptTicket SeedRunningScriptState(string podName)
    {
        var ticket = new ScriptTicket(Guid.NewGuid().ToString("N"));
        var workDir = Path.Combine(_workspace, ticket.TaskId);
        Directory.CreateDirectory(workDir);

        ScriptStateFile.Write(workDir, new ScriptStateFile { TicketId = ticket.TaskId, PodName = podName, EosMarkerToken = "eos", Isolation = "NoIsolation", CreatedAt = DateTimeOffset.UtcNow });
        new ScriptStateStore(workDir).Save(new ScriptState { TicketId = ticket.TaskId, Progress = ScriptProgress.Running, CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow });

        return ticket;
    }

    private void SetupPodNotFound()
    {
        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new k8s.Autorest.HttpOperationException { Response = new k8s.Autorest.HttpResponseMessageWrapper(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), string.Empty) });
    }

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
