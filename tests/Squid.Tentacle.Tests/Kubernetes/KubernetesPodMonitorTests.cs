using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using k8s.Models;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesPodMonitorTests : IDisposable
{
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly KubernetesPodManager _podManager;
    private readonly ScriptPodService _scriptPodService;
    private readonly KubernetesPodMonitor _monitor;
    private readonly string _tempWorkspace;

    public KubernetesPodMonitorTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-monitor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);

        DiskSpaceChecker.Enabled = false;

        _ops = new Mock<IKubernetesPodOperations>();

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        _ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });

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
            OrphanCleanupMinutes = 10
        };

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };

        _podManager = new KubernetesPodManager(_ops.Object, kubernetesSettings);
        _scriptPodService = new ScriptPodService(tentacleSettings, kubernetesSettings, _podManager);
        _monitor = new KubernetesPodMonitor(_podManager, _scriptPodService, tentacleSettings, kubernetesSettings);
    }

    // ========== CleanupOrphanedPods — Terminated ==========

    [Fact]
    public void CleanupOrphanedPods_TerminatedPodOlderThanOrphanAge_Deleted()
    {
        SetupManagedPods(MakeTerminatedPod("orphan-pod", "no-ticket", DateTime.UtcNow.AddMinutes(-15)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod("orphan-pod", "test-ns"), Times.Once);
    }

    [Fact]
    public void CleanupOrphanedPods_TerminatedPodWithinOrphanAge_NotDeleted()
    {
        SetupManagedPods(MakeTerminatedPod("young-pod", "no-ticket", DateTime.UtcNow.AddMinutes(-5)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CleanupOrphanedPods_TerminatedPodWithActiveTicket_NotDeleted()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;

        SetupManagedPods(MakeTerminatedPod("active-pod", ticketId, DateTime.UtcNow.AddMinutes(-15)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod("active-pod", It.IsAny<string>()), Times.Never);
    }

    // ========== CleanupOrphanedPods — Stale Running ==========

    [Fact]
    public void CleanupStaleRunningPods_NoTicket_Deleted()
    {
        SetupManagedPods(MakeRunningPod("stale-running", "no-ticket", DateTime.UtcNow.AddMinutes(-15)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod("stale-running", "test-ns"), Times.Once);
    }

    [Fact]
    public void CleanupStaleRunningPods_WithActiveTicket_NotDeleted()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;

        SetupManagedPods(MakeRunningPod("active-running", ticketId, DateTime.UtcNow.AddMinutes(-15)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod("active-running", It.IsAny<string>()), Times.Never);
    }

    // ========== CleanupOrphanedWorkspaces ==========

    [Fact]
    public void CleanupOrphanedWorkspaces_StaleDir_Deleted()
    {
        var orphanDir = Path.Combine(_tempWorkspace, "orphan-ticket-id");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "script.sh"), "echo stale");

        // Backdate the directory modification time
        Directory.SetLastWriteTimeUtc(orphanDir, DateTime.UtcNow.AddMinutes(-15));

        InvokeCleanup();

        Directory.Exists(orphanDir).ShouldBeFalse();
    }

    // ========== Helpers ==========

    private void InvokeCleanup()
    {
        // Use reflection to call the private cleanup methods
        var type = typeof(KubernetesPodMonitor);

        var cleanupPods = type.GetMethod("CleanupOrphanedPods", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        cleanupPods?.Invoke(_monitor, null);

        var cleanupWorkspaces = type.GetMethod("CleanupOrphanedWorkspaces", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        cleanupWorkspaces?.Invoke(_monitor, null);
    }

    private void SetupManagedPods(params V1Pod[] pods)
    {
        _ops.Setup(o => o.ListPods("test-ns", It.Is<string>(s => s.Contains("managed-by"))))
            .Returns(new V1PodList { Items = pods.ToList() });
    }

    private static V1Pod MakeTerminatedPod(string name, string ticketId, DateTime finishedAt)
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus
            {
                Phase = "Succeeded",
                ContainerStatuses = new List<V1ContainerStatus>
                {
                    new()
                    {
                        Name = "script",
                        State = new V1ContainerState
                        {
                            Terminated = new V1ContainerStateTerminated { FinishedAt = finishedAt }
                        }
                    }
                }
            }
        };
    }

    private static V1Pod MakeRunningPod(string name, string ticketId, DateTime startedAt)
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus
            {
                Phase = "Running",
                StartTime = startedAt
            }
        };
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null);
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
