using System;
using System.Collections.Generic;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesEventMonitorTests
{
    private readonly Mock<IKubernetesPodOperations> _podOps = new();
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "squid-ns" };

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

    private static Corev1Event CreateEvent(string podName, string reason, DateTime timestamp)
    {
        return new Corev1Event
        {
            InvolvedObject = new V1ObjectReference { Name = podName, Kind = "Pod" },
            Reason = reason,
            Message = $"Test event: {reason}",
            LastTimestamp = timestamp
        };
    }
}
