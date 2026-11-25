using System;
using System.Collections.Generic;
using System.IO;
using k8s.Models;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// Fidelity tier: MEDIUM-MOCK — the real <see cref="ScriptPodService"/> over a mocked Kubernetes API. A queued script leaves the pending queue
/// for the in-flight set: the in-flight flag must already be set when the script is removed from the queue, otherwise a lock-free status read
/// landing between the two finds it in neither and reports it lost or unknown (the server re-dispatches a script that is about to run).
/// </summary>
[Collection(TentacleMetricsCollection.Name)]
public class ScriptPodServiceDequeueOrderTests : IDisposable
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly KubernetesSettings _kubernetesSettings;
    private readonly TentacleSettings _tentacleSettings;
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"squid-pod-dequeue-{Guid.NewGuid():N}");

    public ScriptPodServiceDequeueOrderTests()
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
    public void ProcessPendingScripts_TheInFlightFlagIsSetBeforeTheScriptLeavesThePendingQueue()
    {
        var service = new ScriptPodService(_tentacleSettings, _kubernetesSettings, new KubernetesPodManager(_ops.Object, _kubernetesSettings));
        var holder = NewCommand();
        var queued = NewCommand();
        service.StartScript(holder).State.ShouldBe(ScriptStateV2.Running);
        service.StartScript(queued).State.ShouldBe(ScriptStateV2.Pending);
        var hookRan = false;
        ScriptStatusResponseV2? seen = null;

        // Between the two steps a lock-free status read must still find the script (queued or launching), never lost or unknown.
        service.BetweenStartMarkAndDequeueHookForTests = () =>
        {
            hookRan = true;
            seen = service.GetStatus(new ScriptStatusRequestV2(queued.ScriptTicket, 0));
        };

        service.CancelScript(new CancelScriptCommandV2(holder.ScriptTicket, 0));

        hookRan.ShouldBeTrue("releasing the holder's mutex must dequeue the queued script");
        seen!.State.ShouldBe(ScriptStateV2.Pending);
    }

    private static StartScriptCommandV2 NewCommand()
        => new() { ScriptTicket = new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptBody = "echo hi", Isolation = ScriptIsolationLevel.FullIsolation, IsolationMutexName = "dequeue-mx", ScriptIsolationMutexTimeout = TimeSpan.FromMinutes(5) };

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); } catch { }
    }
}
