using System;
using System.Collections.Generic;
using System.IO;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesPodMonitorIntegrationTests : IDisposable
{
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly string _tempWorkspace;

    public KubernetesPodMonitorIntegrationTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-integration-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);

        DiskSpaceChecker.Enabled = false;

        _ops = new Mock<IKubernetesPodOperations>();
        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);
        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
    }

    [Fact]
    public void FailPendingPods_FullLifecycle_MutexReleasedAndPendingLaunched()
    {
        var kubernetesSettings = new KubernetesSettings
        {
            TentacleNamespace = "test-ns",
            ScriptPodImage = "test-image:latest",
            ScriptPodServiceAccount = "test-sa",
            ScriptPodTimeoutSeconds = 60,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "test-pvc",
            PendingPodTimeoutMinutes = 5
        };

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var podManager = new KubernetesPodManager(_ops.Object, kubernetesSettings);
        var service = new ScriptPodService(tentacleSettings, kubernetesSettings, podManager);
        var monitor = new KubernetesPodMonitor(podManager, service, tentacleSettings, kubernetesSettings);

        // Step 1: Start an isolated script — acquires mutex
        var command = new StartScriptCommand("echo first", ScriptIsolationLevel.FullIsolation, TimeSpan.FromMinutes(5), "integration-mutex", Array.Empty<string>(), null);
        var ticket1 = service.StartScript(command);
        var ticketId1 = ticket1.TaskId;
        var podName1 = service.ActiveScripts[ticketId1].PodName;

        // Step 2: Start a second isolated script — should be pending
        var ticket2 = service.StartScript(command);
        service.PendingScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();
        service.ActiveScripts.ContainsKey(ticketId1).ShouldBeTrue();

        // Step 3: Simulate pod stuck in Pending for longer than timeout
        _ops.Setup(o => o.ListPods("test-ns", It.Is<string>(s => s.Contains("managed-by"))))
            .Returns(new V1PodList
            {
                Items = new List<V1Pod>
                {
                    new()
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Name = podName1,
                            CreationTimestamp = DateTime.UtcNow.AddMinutes(-10),
                            Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId1 }
                        },
                        Status = new V1PodStatus { Phase = "Pending" }
                    }
                }
            });

        _ops.Setup(o => o.ReadPodStatus(podName1, "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Pending" } });

        // Step 4: Run FailPendingPods
        monitor.FailPendingPods();

        // Step 5: Verify ticket1 was failed
        service.ActiveScripts.ContainsKey(ticketId1).ShouldBeFalse();

        var status1 = service.GetStatus(new ScriptStatusRequest(ticket1, 0));
        status1.State.ShouldBe(ProcessState.Complete);
        status1.ExitCode.ShouldBe(ScriptExitCodes.Timeout);

        // Step 6: Verify ticket2 was dequeued and launched (mutex released)
        service.PendingScripts.ShouldBeEmpty();
        service.ActiveScripts.ContainsKey(ticket2.TaskId).ShouldBeTrue();

        // Step 7: Pod was deleted
        _ops.Verify(o => o.DeletePod(podName1, "test-ns", It.IsAny<int?>()), Times.Once);

        // Step 8: New pod was created for ticket2
        _ops.Verify(o => o.CreatePod(It.IsAny<V1Pod>(), "test-ns"), Times.Exactly(2));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
                Directory.Delete(_tempWorkspace, recursive: true);
        }
        catch { }
    }
}
