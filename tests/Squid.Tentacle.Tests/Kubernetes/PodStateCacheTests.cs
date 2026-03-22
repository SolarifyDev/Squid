using k8s;
using k8s.Models;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class PodStateCacheTests
{
    private readonly PodStateCache _cache = new();

    [Fact]
    public void Added_PodInCache()
    {
        var pod = MakePod("pod-1", "ticket-1", "Running");

        _cache.HandleWatchEvent(WatchEventType.Added, pod);

        _cache.TryGetPod("pod-1", out var cached).ShouldBeTrue();
        cached.Status.Phase.ShouldBe("Running");
    }

    [Fact]
    public void Modified_PodUpdated()
    {
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-1", "ticket-1", "Pending"));
        _cache.HandleWatchEvent(WatchEventType.Modified, MakePod("pod-1", "ticket-1", "Running"));

        _cache.TryGetPod("pod-1", out var cached).ShouldBeTrue();
        cached.Status.Phase.ShouldBe("Running");
    }

    [Fact]
    public void Deleted_PodRemoved()
    {
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-1", "ticket-1", "Running"));
        _cache.HandleWatchEvent(WatchEventType.Deleted, MakePod("pod-1", "ticket-1", "Succeeded"));

        _cache.TryGetPod("pod-1", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGetPod_Hit_ReturnsTrue()
    {
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-1", "ticket-1", "Running"));

        _cache.TryGetPod("pod-1", out var pod).ShouldBeTrue();
        pod.ShouldNotBeNull();
    }

    [Fact]
    public void TryGetPod_Miss_ReturnsFalse()
    {
        _cache.TryGetPod("nonexistent", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGetPodByTicket_FindsByLabel()
    {
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-1", "ticket-abc", "Running"));
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-2", "ticket-xyz", "Running"));

        _cache.TryGetPodByTicket("ticket-xyz", out var pod).ShouldBeTrue();
        pod.Metadata.Name.ShouldBe("pod-2");
    }

    [Fact]
    public void TryGetPodByTicket_NotFound_ReturnsFalse()
    {
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-1", "ticket-abc", "Running"));

        _cache.TryGetPodByTicket("ticket-missing", out _).ShouldBeFalse();
    }

    [Fact]
    public void Invalidate_ClearsAll()
    {
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-1", "t1", "Running"));
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("pod-2", "t2", "Running"));

        _cache.Invalidate();

        _cache.Count.ShouldBe(0);
    }

    [Fact]
    public void Populate_BulkLoads()
    {
        var pods = new List<V1Pod>
        {
            MakePod("pod-1", "t1", "Running"),
            MakePod("pod-2", "t2", "Pending"),
            MakePod("pod-3", "t3", "Succeeded")
        };

        _cache.Populate(pods);

        _cache.Count.ShouldBe(3);
        _cache.TryGetPod("pod-2", out var p).ShouldBeTrue();
        p.Status.Phase.ShouldBe("Pending");
    }

    [Fact]
    public void Populate_ReplacesExistingEntries()
    {
        _cache.HandleWatchEvent(WatchEventType.Added, MakePod("old-pod", "t0", "Running"));

        _cache.Populate(new[] { MakePod("new-pod", "t1", "Pending") });

        _cache.Count.ShouldBe(1);
        _cache.TryGetPod("old-pod", out _).ShouldBeFalse();
        _cache.TryGetPod("new-pod", out _).ShouldBeTrue();
    }

    private static V1Pod MakePod(string name, string ticketId, string phase)
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
}
