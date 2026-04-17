using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using k8s.Models;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Health;

namespace Squid.Tentacle.Tests.Kubernetes;

[Collection(TentacleMetricsCollection.Name)]
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

        _ops.Verify(o => o.DeletePod("orphan-pod", "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public void CleanupOrphanedPods_TerminatedPodWithinOrphanAge_NotDeleted()
    {
        SetupManagedPods(MakeTerminatedPod("young-pod", "no-ticket", DateTime.UtcNow.AddMinutes(-5)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void CleanupOrphanedPods_TerminatedPodWithActiveTicket_NotDeleted()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test")).Ticket;
        var ticketId = ticket.TaskId;

        SetupManagedPods(MakeTerminatedPod("active-pod", ticketId, DateTime.UtcNow.AddMinutes(-15)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod("active-pod", It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    // ========== CleanupOrphanedPods — Stale Running ==========

    [Fact]
    public void CleanupStaleRunningPods_NoTicket_Deleted()
    {
        SetupManagedPods(MakeRunningPod("stale-running", "no-ticket", DateTime.UtcNow.AddMinutes(-15)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod("stale-running", "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public void CleanupStaleRunningPods_WithActiveTicket_NotDeleted()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test")).Ticket;
        var ticketId = ticket.TaskId;

        SetupManagedPods(MakeRunningPod("active-running", ticketId, DateTime.UtcNow.AddMinutes(-15)));

        InvokeCleanup();

        _ops.Verify(o => o.DeletePod("active-running", It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
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

    // ========== FailPendingPods ==========

    [Fact]
    public void FailPendingPods_PodStillPending_Deleted()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test")).Ticket;
        var ticketId = ticket.TaskId;

        var pod = MakePendingPod("squid-script-stuck", ticketId, DateTime.UtcNow.AddMinutes(-10));

        SetupManagedPods(pod);

        _ops.Setup(o => o.ReadPodStatus("squid-script-stuck", "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Pending" } });

        _monitor.FailPendingPods();

        _ops.Verify(o => o.DeletePod("squid-script-stuck", "test-ns", It.IsAny<int?>()), Times.Once);
    }

    [Fact]
    public void FailPendingPods_PodTransitionedToRunning_NotDeleted()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test")).Ticket;
        var ticketId = ticket.TaskId;

        var pod = MakePendingPod("squid-script-stuck", ticketId, DateTime.UtcNow.AddMinutes(-10));

        SetupManagedPods(pod);

        // Re-check returns Running
        _ops.Setup(o => o.ReadPodStatus("squid-script-stuck", "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Running" } });

        _monitor.FailPendingPods();

        _ops.Verify(o => o.DeletePod("squid-script-stuck", It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void FailPendingPods_PodTransitionedToSucceeded_NotDeleted()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test")).Ticket;
        var ticketId = ticket.TaskId;

        var pod = MakePendingPod("squid-script-stuck", ticketId, DateTime.UtcNow.AddMinutes(-10));

        SetupManagedPods(pod);

        _ops.Setup(o => o.ReadPodStatus("squid-script-stuck", "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Succeeded" } });

        _monitor.FailPendingPods();

        _ops.Verify(o => o.DeletePod("squid-script-stuck", It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void FailPendingPods_NonPendingPhase_Skipped()
    {
        SetupManagedPods(MakeRunningPod("running-pod", "ticket-x", DateTime.UtcNow.AddMinutes(-10)));

        _monitor.FailPendingPods();

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void FailPendingPods_NoTicketLabel_Skipped()
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "squid-script-no-ticket",
                Labels = new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "kubernetes-agent" },
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-10)
            },
            Status = new V1PodStatus { Phase = "Pending" }
        };

        SetupManagedPods(pod);

        _monitor.FailPendingPods();

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void FailPendingPods_WithinTimeout_Skipped()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test")).Ticket;
        var ticketId = ticket.TaskId;

        var pod = MakePendingPod("squid-script-new", ticketId, DateTime.UtcNow.AddMinutes(-1));

        SetupManagedPods(pod);

        _monitor.FailPendingPods();

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public void FailPendingPods_ReleasesMutex_AfterDeletion()
    {
        var command = new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "echo test", ScriptIsolationLevel.FullIsolation, TimeSpan.FromMinutes(5), "test-mutex", Array.Empty<string>(), null, TimeSpan.Zero);
        _scriptPodService.StartScript(command);
        var ticket = command.ScriptTicket;
        var ticketId = ticket.TaskId;

        _scriptPodService.MutexLocks.ContainsKey(ticketId).ShouldBeTrue();

        var pod = MakePendingPod("squid-script-stuck", ticketId, DateTime.UtcNow.AddMinutes(-10));
        SetupManagedPods(pod);

        _ops.Setup(o => o.ReadPodStatus("squid-script-stuck", "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Pending" } });

        _monitor.FailPendingPods();

        _scriptPodService.MutexLocks.ContainsKey(ticketId).ShouldBeFalse();
    }

    [Fact]
    public void FailPendingPods_MutexReleased_PendingScriptCanStart()
    {
        var command1 = new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "echo test", ScriptIsolationLevel.FullIsolation, TimeSpan.FromMinutes(5), "shared-mutex", Array.Empty<string>(), null, TimeSpan.Zero);
        var command2 = new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            "echo test", ScriptIsolationLevel.FullIsolation, TimeSpan.FromMinutes(5), "shared-mutex", Array.Empty<string>(), null, TimeSpan.Zero);
        _scriptPodService.StartScript(command1);
        _scriptPodService.StartScript(command2);
        var ticketId1 = command1.ScriptTicket.TaskId;

        _scriptPodService.ActiveScripts.ContainsKey(ticketId1).ShouldBeTrue();
        _scriptPodService.PendingScripts.ContainsKey(command2.ScriptTicket.TaskId).ShouldBeTrue();

        var pod = MakePendingPod("squid-script-stuck", ticketId1, DateTime.UtcNow.AddMinutes(-10));
        SetupManagedPods(pod);

        _ops.Setup(o => o.ReadPodStatus("squid-script-stuck", "test-ns"))
            .Returns(new V1Pod { Status = new V1PodStatus { Phase = "Pending" } });

        _monitor.FailPendingPods();

        // Pending script should have been launched after mutex was released
        _scriptPodService.PendingScripts.ShouldBeEmpty();
        _scriptPodService.ActiveScripts.ContainsKey(command2.ScriptTicket.TaskId).ShouldBeTrue();
    }

    // ========== Leader Election Gating ==========

    [Fact]
    public void RunCleanupCycle_NotLeader_SkipsCleanup()
    {
        var leaderElection = CreateLeaderElection(isLeader: false);
        var monitor = CreateMonitorWithLeaderElection(leaderElection);

        SetupManagedPods(MakeTerminatedPod("orphan-pod", "no-ticket", DateTime.UtcNow.AddMinutes(-15)));

        monitor.RunCleanupCycle();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.Is<string>(s => s.Contains("managed-by"))), Times.Never);
    }

    [Fact]
    public void RunCleanupCycle_IsLeader_PerformsCleanup()
    {
        var leaderElection = CreateLeaderElection(isLeader: true);
        var monitor = CreateMonitorWithLeaderElection(leaderElection);

        SetupManagedPods();

        monitor.RunCleanupCycle();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.Is<string>(s => s.Contains("managed-by"))), Times.AtLeastOnce);
    }

    [Fact]
    public void RunCleanupCycle_NoLeaderElection_AlwaysCleanup()
    {
        SetupManagedPods();

        _monitor.RunCleanupCycle();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.Is<string>(s => s.Contains("managed-by"))), Times.AtLeastOnce);
    }

    // ========== RunPendingCheck ==========

    [Fact]
    public void RunPendingCheck_IsLeader_CallsFailPendingPods()
    {
        var leaderElection = CreateLeaderElection(isLeader: true);
        var monitor = CreateMonitorWithLeaderElection(leaderElection);

        SetupManagedPods();

        monitor.RunPendingCheck();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public void RunPendingCheck_NotLeader_Skips()
    {
        var leaderElection = CreateLeaderElection(isLeader: false);
        var monitor = CreateMonitorWithLeaderElection(leaderElection);

        monitor.RunPendingCheck();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RunPendingCheck_NoLeaderElection_Proceeds()
    {
        SetupManagedPods();

        _monitor.RunPendingCheck();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
    }

    // ========== RunOrphanCleanup ==========

    [Fact]
    public void RunOrphanCleanup_IsLeader_CallsCleanupMethods()
    {
        var leaderElection = CreateLeaderElection(isLeader: true);
        var monitor = CreateMonitorWithLeaderElection(leaderElection);

        SetupManagedPods();

        monitor.RunOrphanCleanup();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public void RunOrphanCleanup_NotLeader_Skips()
    {
        var leaderElection = CreateLeaderElection(isLeader: false);
        var monitor = CreateMonitorWithLeaderElection(leaderElection);

        monitor.RunOrphanCleanup();

        _ops.Verify(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ========== Helpers ==========

    private static KubernetesLeaderElection CreateLeaderElection(bool isLeader)
    {
        var leaderElection = new KubernetesLeaderElection(null, new KubernetesSettings { TentacleNamespace = "test-ns" }, "test-identity");

        var field = typeof(KubernetesLeaderElection).GetField("_isLeader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(leaderElection, isLeader);

        return leaderElection;
    }

    private KubernetesPodMonitor CreateMonitorWithLeaderElection(KubernetesLeaderElection leaderElection)
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
            OrphanCleanupMinutes = 10
        };

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };

        return new KubernetesPodMonitor(_podManager, _scriptPodService, tentacleSettings, kubernetesSettings, leaderElection: leaderElection);
    }

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

    private static V1Pod MakePendingPod(string name, string ticketId, DateTime createdAt)
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId },
                CreationTimestamp = createdAt
            },
            Status = new V1PodStatus { Phase = "Pending" }
        };
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
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
