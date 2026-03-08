using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Lifecycle;

namespace Squid.Tentacle.Tests.Kubernetes;

[Trait("Category", TentacleTestCategories.Lifecycle)]
public class KubernetesEventMonitorLifecycleTests : TimedTestBase
{
    [Fact]
    public async Task BackgroundTask_Starts_And_Cancels_Gracefully()
    {
        var podOps = new Mock<IKubernetesPodOperations>();
        podOps.Setup(o => o.ListEvents(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Corev1EventList { Items = new List<Corev1Event>() });

        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };
        var monitor = new KubernetesEventMonitor(podOps.Object, settings);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var running = TentacleLifecycleHarness.StartBackgroundTasks(new[] { monitor }, cts.Token);
        running.Count.ShouldBe(1);

        // Allow first poll to execute
        await Task.Delay(200, TestCancellationToken);

        // Verify it actually called ListEvents
        podOps.Verify(o => o.ListEvents("test-ns", It.IsAny<string>(), It.IsAny<string>()), Times.AtLeastOnce);

        cts.Cancel();

        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(3), TestCancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    [Fact]
    public async Task BackgroundTask_SurvivesApiFailures()
    {
        var podOps = new Mock<IKubernetesPodOperations>();
        var callCount = 0;
        podOps.Setup(o => o.ListEvents(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount <= 2) throw new Exception("API unavailable");
                return new Corev1EventList { Items = new List<Corev1Event>() };
            });

        var settings = new KubernetesSettings { TentacleNamespace = "test-ns" };
        var monitor = new KubernetesEventMonitor(podOps.Object, settings);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var running = TentacleLifecycleHarness.StartBackgroundTasks(new[] { monitor }, cts.Token);

        // Allow first poll (which will fail)
        await Task.Delay(200, TestCancellationToken);

        // Task should still be running despite API failures
        running[0].IsCompleted.ShouldBeFalse("Monitor should survive API failures");

        cts.Cancel();

        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(3), TestCancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }
}
