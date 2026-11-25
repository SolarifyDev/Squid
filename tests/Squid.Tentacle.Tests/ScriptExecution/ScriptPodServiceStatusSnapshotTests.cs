using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using k8s.Models;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> over a mocked Kubernetes API. The pod finishes while a status
/// poll is reading its logs (the log read is where the fake flips the pod to Succeeded), pinning the window where a poll that drained
/// the logs of a running pod could still report it Complete, without the lines written just before it exited — and, since a Complete
/// poll releases the isolation mutex, free the lock for the next script while those lines were still undelivered.
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceStatusSnapshotTests : IDisposable
{
    private readonly TentacleSettings _tentacleSettings;
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-snapshot-{Guid.NewGuid():N}");

    public ScriptPodServiceStatusSnapshotTests()
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
    }

    [Fact]
    public void GetStatusV1_PodFinishesWhileTheLogsAreBeingRead_ReportsRunning_ThenCompleteWithTheFinalLines()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);
        FinishPodDuringTheFirstLogRead();

        var first = service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));
        var second = service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, first.NextLogSequence));

        first.State.ShouldBe(ProcessState.Running, "a Complete built from logs read while the pod was still running misses the final lines");
        Text(first.Logs).ShouldContain("first-line");
        second.State.ShouldBe(ProcessState.Complete);
        Text(second.Logs).ShouldContain("last-line");
    }

    [Fact]
    public void GetStatusV1_PodFinishesWhileTheLogsAreBeingRead_KeepsTheIsolationMutexUntilTheFinalLinesAreDelivered()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);
        FinishPodDuringTheFirstLogRead();

        var first = service.GetStatus(new ScriptStatusRequest(command.ScriptTicket, 0));

        first.State.ShouldBe(ProcessState.Running);
        service.MutexLocks.ContainsKey(command.ScriptTicket.TaskId).ShouldBeTrue("a Complete poll releases the lock; it must not fire before the final lines were drained");
    }

    [Fact]
    public void GetStatusV2_PodFinishesWhileTheLogsAreBeingRead_ReportsRunning_ThenCompletedWithTheFinalLines()
    {
        var service = NewService();
        var command = NewCommand();
        service.StartScript(command);
        FinishPodDuringTheFirstLogRead();

        var first = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, 0));
        var second = service.GetStatus(new ScriptStatusRequestV2(command.ScriptTicket, first.NextLogSequence));

        first.State.ShouldBe(ScriptStateV2.Running);
        second.State.ShouldBe(ScriptStateV2.Completed);
        Text(second.Logs).ShouldContain("last-line");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ScriptPodService NewService() => new(_tentacleSettings, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings));

    private static StartScriptCommand NewCommand()
        => new(new ScriptTicket(Guid.NewGuid().ToString("N")), "echo hi", ScriptIsolationLevel.FullIsolation, TimeSpan.FromMinutes(5), "pod-snapshot-mx", Array.Empty<string>(), null, TimeSpan.Zero);

    // The first log read returns what the pod had written so far and, as it runs, the pod exits; every later read has the final line too.
    private void FinishPodDuringTheFirstLogRead()
    {
        var reads = 0;

        SetupPod("Running", terminated: false);

        _ops.Setup(o => o.ReadPodLog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>())).Returns(() =>
        {
            if (reads++ > 0) return Stream("first-line\nlast-line\n");

            SetupPod("Succeeded", terminated: true);

            return Stream("first-line\n");
        });
    }

    private void SetupPod(string phase, bool terminated)
    {
        var state = terminated ? new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 0 } } : new V1ContainerState { Running = new V1ContainerStateRunning() };

        _ops.Setup(o => o.ReadPodStatus(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = phase, ContainerStatuses = new List<V1ContainerStatus> { new() { Name = "script", State = state } } } });
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Text(IEnumerable<ProcessOutput> logs) => string.Join("\n", logs.Select(l => l.Text));

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
