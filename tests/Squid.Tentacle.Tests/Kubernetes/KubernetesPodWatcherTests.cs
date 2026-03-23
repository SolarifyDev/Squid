using System.Runtime.CompilerServices;
using k8s;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesPodWatcherTests
{
    private readonly KubernetesSettings _settings = new()
    {
        TentacleNamespace = "squid-ns",
        ReleaseName = "squid-prod"
    };

    [Fact]
    public async Task RunAsync_PodSucceeded_LogsCompletion()
    {
        var pod = BuildPod("test-pod", "Succeeded", "ticket-123");
        var events = new List<(WatchEventType, V1Pod)> { (WatchEventType.Modified, pod) };
        var cts = new CancellationTokenSource();

        var ops = CreateMockOps(events, cts);
        var watcher = new KubernetesPodWatcher(ops.Object, _settings);

        await watcher.RunAsync(cts.Token);
    }

    [Fact]
    public async Task RunAsync_PodFailed_LogsCompletion()
    {
        var pod = BuildPod("test-pod", "Failed", "ticket-456");
        var events = new List<(WatchEventType, V1Pod)> { (WatchEventType.Modified, pod) };
        var cts = new CancellationTokenSource();

        var ops = CreateMockOps(events, cts);
        var watcher = new KubernetesPodWatcher(ops.Object, _settings);

        await watcher.RunAsync(cts.Token);
    }

    [Fact]
    public async Task RunAsync_PodRunning_IgnoresEvent()
    {
        var pod = BuildPod("test-pod", "Running", "ticket-789");
        var events = new List<(WatchEventType, V1Pod)> { (WatchEventType.Modified, pod) };
        var cts = new CancellationTokenSource();

        var ops = CreateMockOps(events, cts);
        var watcher = new KubernetesPodWatcher(ops.Object, _settings);

        await watcher.RunAsync(cts.Token);
        // No exception — event is silently ignored
    }

    [Fact]
    public void HandlePodEvent_PodWithoutTicketLabel_IgnoresEvent()
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "test-pod", Labels = new Dictionary<string, string>() },
            Status = new V1PodStatus { Phase = "Succeeded" }
        };

        // Should not throw
        KubernetesPodWatcher.HandlePodEvent(WatchEventType.Modified, pod);
    }

    [Fact]
    public async Task RunAsync_StreamDisconnects_Reconnects()
    {
        var callCount = 0;
        var cts = new CancellationTokenSource();
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.WatchPodsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns((string ns, string label, CancellationToken ct, string rv) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new IOException("stream disconnected");
                cts.Cancel();
                return EmptyAsyncEnumerable(ct);
            });

        var watcher = new KubernetesPodWatcher(ops.Object, _settings);

        await watcher.RunAsync(cts.Token);

        callCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RunAsync_Cancellation_ExitsGracefully()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var ops = new Mock<IKubernetesPodOperations>();
        ops.Setup(o => o.WatchPodsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns((string ns, string label, CancellationToken ct, string rv) => EmptyAsyncEnumerable(ct));

        var watcher = new KubernetesPodWatcher(ops.Object, _settings);

        await Should.NotThrowAsync(() => watcher.RunAsync(cts.Token));
    }

    [Fact]
    public async Task RunAsync_Reconnect_PassesLastResourceVersion()
    {
        var capturedResourceVersions = new List<string>();
        var callCount = 0;
        var cts = new CancellationTokenSource();
        var ops = new Mock<IKubernetesPodOperations>();
        var cache = new PodStateCache();

        // ListPods returns resourceVersion "rv-500"
        ops.Setup(o => o.ListPods(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new V1PodList
            {
                Metadata = new V1ListMeta { ResourceVersion = "rv-500" },
                Items = new List<V1Pod>()
            });

        ops.Setup(o => o.WatchPodsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns((string ns, string label, CancellationToken ct, string rv) =>
            {
                capturedResourceVersions.Add(rv);
                callCount++;
                if (callCount == 1)
                    throw new IOException("stream disconnected");
                cts.Cancel();
                return EmptyAsyncEnumerable(ct);
            });

        var watcher = new KubernetesPodWatcher(ops.Object, _settings, cache);
        await watcher.RunAsync(cts.Token);

        capturedResourceVersions.Count.ShouldBeGreaterThanOrEqualTo(2);
        capturedResourceVersions[0].ShouldBe("rv-500");
        capturedResourceVersions[1].ShouldBe("rv-500");
    }

    private static V1Pod BuildPod(string name, string phase, string ticketId)
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                Labels = new Dictionary<string, string> { ["squid.io/ticket-id"] = ticketId }
            },
            Status = new V1PodStatus { Phase = phase }
        };
    }

    private Mock<IKubernetesPodOperations> CreateMockOps(List<(WatchEventType, V1Pod)> events, CancellationTokenSource cts)
    {
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.WatchPodsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns((string ns, string label, CancellationToken ct, string rv) =>
            {
                cts.Cancel();
                return ToAsyncEnumerable(events, ct);
            });

        return ops;
    }

    private static async IAsyncEnumerable<(WatchEventType, V1Pod)> ToAsyncEnumerable(List<(WatchEventType, V1Pod)> items, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) yield break;
            yield return item;
            await Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<(WatchEventType, V1Pod)> EmptyAsyncEnumerable([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
