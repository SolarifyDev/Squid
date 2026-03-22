using System.Collections.Concurrent;
using k8s;
using k8s.Models;

namespace Squid.Tentacle.Kubernetes;

public class PodStateCache
{
    private readonly ConcurrentDictionary<string, V1Pod> _cache = new(StringComparer.Ordinal);

    public int Count => _cache.Count;

    public void HandleWatchEvent(WatchEventType type, V1Pod pod)
    {
        var podName = pod.Metadata?.Name;
        if (string.IsNullOrEmpty(podName)) return;

        if (type is WatchEventType.Added or WatchEventType.Modified)
            _cache[podName] = pod;
        else if (type is WatchEventType.Deleted)
            _cache.TryRemove(podName, out _);
    }

    public bool TryGetPod(string podName, out V1Pod pod)
        => _cache.TryGetValue(podName, out pod);

    public bool TryGetPodByTicket(string ticketId, out V1Pod pod)
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.Metadata?.Labels != null &&
                kvp.Value.Metadata.Labels.TryGetValue("squid.io/ticket-id", out var tid) &&
                string.Equals(tid, ticketId, StringComparison.Ordinal))
            {
                pod = kvp.Value;
                return true;
            }
        }

        pod = null;
        return false;
    }

    public void Invalidate()
    {
        _cache.Clear();
    }

    public void Populate(IEnumerable<V1Pod> pods)
    {
        _cache.Clear();

        foreach (var pod in pods)
        {
            var name = pod.Metadata?.Name;
            if (!string.IsNullOrEmpty(name))
                _cache[name] = pod;
        }
    }
}
