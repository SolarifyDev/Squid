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

public class PendingPodWatchdogTests : IDisposable
{
    private readonly Mock<IKubernetesPodOperations> _ops;
    private readonly KubernetesPodManager _podManager;
    private readonly ScriptPodService _scriptPodService;
    private readonly KubernetesPodMonitor _monitor;
    private readonly string _tempWorkspace;

    public PendingPodWatchdogTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-watchdog-test-{Guid.NewGuid():N}");
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
            PvcClaimName = "test-pvc"
        };

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };

        _podManager = new KubernetesPodManager(_ops.Object, kubernetesSettings);
        _scriptPodService = new ScriptPodService(tentacleSettings, kubernetesSettings, _podManager);
        _monitor = new KubernetesPodMonitor(_podManager, _scriptPodService, tentacleSettings, kubernetesSettings);
    }

    [Fact]
    public void FailPendingPods_PodPendingBeyondTimeout_RemovesFromActiveAndInjectsTimeout()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;
        var podName = _scriptPodService.ActiveScripts[ticketId].PodName;

        SetupManagedPods(new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-10),
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus { Phase = "Pending" }
        });

        _monitor.FailPendingPods();

        _scriptPodService.ActiveScripts.ContainsKey(ticketId).ShouldBeFalse();

        var status = _scriptPodService.GetStatus(new ScriptStatusRequest(ticket, 0));

        status.State.ShouldBe(ProcessState.Complete);
        status.ExitCode.ShouldBe(ScriptExitCodes.Timeout);
        status.Logs.ShouldNotBeEmpty();
        status.Logs[0].Source.ShouldBe(ProcessOutputSource.StdErr);
    }

    [Fact]
    public void FailPendingPods_PodPendingWithinTimeout_DoesNotRemove()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;
        var podName = _scriptPodService.ActiveScripts[ticketId].PodName;

        SetupManagedPods(new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-2),
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus { Phase = "Pending" }
        });

        _monitor.FailPendingPods();

        _scriptPodService.ActiveScripts.ContainsKey(ticketId).ShouldBeTrue();
    }

    [Fact]
    public void FailPendingPods_RunningPod_NotAffected()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;
        var podName = _scriptPodService.ActiveScripts[ticketId].PodName;

        SetupManagedPods(new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-10),
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus { Phase = "Running" }
        });

        _monitor.FailPendingPods();

        _scriptPodService.ActiveScripts.ContainsKey(ticketId).ShouldBeTrue();
    }

    [Fact]
    public void FailPendingPods_PodWithNoTicket_Ignored()
    {
        SetupManagedPods(new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "orphan-pod",
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-10),
                Labels = new Dictionary<string, string>()
            },
            Status = new V1PodStatus { Phase = "Pending" }
        });

        _monitor.FailPendingPods();

        _ops.Verify(o => o.DeletePod(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void FailPendingPods_TerminalResult_ConsumedByCompleteScript()
    {
        var ticket = _scriptPodService.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;
        var podName = _scriptPodService.ActiveScripts[ticketId].PodName;

        SetupManagedPods(new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-10),
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus { Phase = "Pending" }
        });

        _monitor.FailPendingPods();

        var result = _scriptPodService.CompleteScript(new CompleteScriptCommand(ticket, 0));

        result.State.ShouldBe(ProcessState.Complete);
        result.ExitCode.ShouldBe(ScriptExitCodes.Timeout);

        var resultAfter = _scriptPodService.GetStatus(new ScriptStatusRequest(ticket, 0));

        resultAfter.ExitCode.ShouldBe(ScriptExitCodes.UnknownResult);
    }

    [Fact]
    public void FailPendingPods_CustomTimeout_RespectsConfiguredValue()
    {
        var customSettings = new KubernetesSettings
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
            PendingPodTimeoutMinutes = 1
        };

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var podManager = new KubernetesPodManager(_ops.Object, customSettings);
        var service = new ScriptPodService(tentacleSettings, customSettings, podManager);
        var monitor = new KubernetesPodMonitor(podManager, service, tentacleSettings, customSettings);

        var ticket = service.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;
        var podName = service.ActiveScripts[ticketId].PodName;

        SetupManagedPods(new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-2),
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus { Phase = "Pending" }
        });

        monitor.FailPendingPods();

        service.ActiveScripts.ContainsKey(ticketId).ShouldBeFalse();
    }

    [Fact]
    public void FailPendingPods_CustomTimeout_PodWithinTimeout_NotAffected()
    {
        var customSettings = new KubernetesSettings
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
            PendingPodTimeoutMinutes = 10
        };

        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var podManager = new KubernetesPodManager(_ops.Object, customSettings);
        var service = new ScriptPodService(tentacleSettings, customSettings, podManager);
        var monitor = new KubernetesPodMonitor(podManager, service, tentacleSettings, customSettings);

        var ticket = service.StartScript(MakeCommand("echo test"));
        var ticketId = ticket.TaskId;
        var podName = service.ActiveScripts[ticketId].PodName;

        SetupManagedPods(new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                CreationTimestamp = DateTime.UtcNow.AddMinutes(-6),
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus { Phase = "Pending" }
        });

        monitor.FailPendingPods();

        service.ActiveScripts.ContainsKey(ticketId).ShouldBeTrue();
    }

    private void SetupManagedPods(params V1Pod[] pods)
    {
        _ops.Setup(o => o.ListPods("test-ns", It.Is<string>(s => s.Contains("managed-by"))))
            .Returns(new V1PodList { Items = pods.ToList() });
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
