using System;
using System.Collections.Generic;
using System.IO;
using k8s.Models;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesEventMonitorTests : IDisposable
{
    private readonly Mock<IKubernetesPodOperations> _podOps = new();
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "squid-ns" };
    private readonly string _tempWorkspace;

    public KubernetesEventMonitorTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), $"squid-test-evt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempWorkspace);
    }

    [Fact]
    public void Name_ReturnsKubernetesEventMonitor()
    {
        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings);

        monitor.Name.ShouldBe("KubernetesEventMonitor");
    }

    [Fact]
    public async Task RunAsync_CancelledImmediately_CompletesGracefully()
    {
        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await monitor.RunAsync(cts.Token);
    }

    [Fact]
    public async Task RunAsync_NoEvents_DoesNotThrow()
    {
        _podOps.Setup(o => o.ListEvents(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Corev1EventList { Items = new List<Corev1Event>() });

        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await monitor.RunAsync(cts.Token);
    }

    [Fact]
    public async Task RunAsync_EventsForUnmanagedPods_Ignored()
    {
        var events = new Corev1EventList
        {
            Items = new List<Corev1Event>
            {
                CreateEvent("some-other-pod", "FailedScheduling", DateTime.UtcNow.AddSeconds(1))
            }
        };

        _podOps.Setup(o => o.ListEvents("squid-ns", It.IsAny<string>(), It.IsAny<string>())).Returns(events);

        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Should not throw — just logs nothing
        await monitor.RunAsync(cts.Token);
    }

    [Fact]
    public async Task RunAsync_QueriesCorrectNamespace()
    {
        _podOps.Setup(o => o.ListEvents(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Corev1EventList { Items = new List<Corev1Event>() });

        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await monitor.RunAsync(cts.Token);

        _podOps.Verify(o => o.ListEvents("squid-ns", It.Is<string>(s => s.Contains("Pod")), It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_ApiFailure_DoesNotCrash()
    {
        _podOps.Setup(o => o.ListEvents(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new Exception("API unavailable"));

        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await monitor.RunAsync(cts.Token);
    }

    // ========== Event Injection into Script Logs ==========

    [Fact]
    public void PollEvents_WarningForManagedPod_InjectsIntoActiveScript()
    {
        var scriptPodService = CreateScriptPodService();
        var ctx = new ScriptPodContext("ticket-abc", "squid-script-abc123", "/tmp/work", "marker");
        scriptPodService.ActiveScripts["ticket-abc"] = ctx;

        var events = new Corev1EventList
        {
            Items = new List<Corev1Event>
            {
                CreateEvent("squid-script-abc123", "FailedScheduling", DateTime.UtcNow.AddSeconds(1), "Insufficient cpu")
            }
        };

        _podOps.Setup(o => o.ListEvents("squid-ns", It.IsAny<string>(), It.IsAny<string>())).Returns(events);

        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings, scriptPodService);
        monitor.PollEvents();

        ctx.InjectedEvents.TryDequeue(out var injected).ShouldBeTrue();
        injected.Source.ShouldBe(ProcessOutputSource.StdErr);
        injected.Text.ShouldContain("FailedScheduling");
        injected.Text.ShouldContain("Insufficient cpu");
    }

    [Fact]
    public void PollEvents_WarningForUnmanagedPod_NotInjected()
    {
        var scriptPodService = CreateScriptPodService();

        var events = new Corev1EventList
        {
            Items = new List<Corev1Event>
            {
                CreateEvent("other-pod", "FailedScheduling", DateTime.UtcNow.AddSeconds(1), "Insufficient cpu")
            }
        };

        _podOps.Setup(o => o.ListEvents("squid-ns", It.IsAny<string>(), It.IsAny<string>())).Returns(events);

        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings, scriptPodService);
        monitor.PollEvents();

        scriptPodService.ActiveScripts.ShouldBeEmpty();
    }

    [Fact]
    public void PollEvents_NoActiveScript_NoException()
    {
        var scriptPodService = CreateScriptPodService();

        var events = new Corev1EventList
        {
            Items = new List<Corev1Event>
            {
                CreateEvent("squid-script-orphan12", "OOMKilled", DateTime.UtcNow.AddSeconds(1), "Out of memory")
            }
        };

        _podOps.Setup(o => o.ListEvents("squid-ns", It.IsAny<string>(), It.IsAny<string>())).Returns(events);

        var monitor = new KubernetesEventMonitor(_podOps.Object, _settings, scriptPodService);

        Should.NotThrow(() => monitor.PollEvents());
    }

    private ScriptPodService CreateScriptPodService()
    {
        var tentacleSettings = new TentacleSettings { WorkspacePath = _tempWorkspace };
        var kubernetesSettings = new KubernetesSettings { TentacleNamespace = "squid-ns" };
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList { Items = new List<V1Pod>() });
        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        var podManager = new KubernetesPodManager(ops.Object, kubernetesSettings);
        return new ScriptPodService(tentacleSettings, kubernetesSettings, podManager);
    }

    private static Corev1Event CreateEvent(string podName, string reason, DateTime timestamp, string message = null)
    {
        return new Corev1Event
        {
            InvolvedObject = new V1ObjectReference { Name = podName, Kind = "Pod" },
            Reason = reason,
            Message = message ?? $"Test event: {reason}",
            LastTimestamp = timestamp
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
                Directory.Delete(_tempWorkspace, true);
        }
        catch { }
    }
}
