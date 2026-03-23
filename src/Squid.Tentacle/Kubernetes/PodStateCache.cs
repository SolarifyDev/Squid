using System.Collections.Concurrent;
using k8s;
using k8s.Models;

namespace Squid.Tentacle.Kubernetes;

public class PodStateCache
{
    private ConcurrentDictionary<string, V1Pod> _cache = new(StringComparer.Ordinal);

    public int Count => _cache.Count;

    public void HandleWatchEvent(WatchEventType type, V1Pod pod)
    {
        var key = CacheKey(pod);
        if (string.IsNullOrEmpty(pod.Metadata?.Name)) return;

        if (type is WatchEventType.Added or WatchEventType.Modified)
            _cache[key] = pod;
        else if (type is WatchEventType.Deleted)
            _cache.TryRemove(key, out _);
    }

    public bool TryGetPod(string podName, out V1Pod pod, string ns = null)
        => _cache.TryGetValue(CacheKey(podName, ns), out pod);

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
        Interlocked.Exchange(ref _cache, new ConcurrentDictionary<string, V1Pod>(StringComparer.Ordinal));
    }

    public void Populate(IEnumerable<V1Pod> pods)
    {
        var newCache = new ConcurrentDictionary<string, V1Pod>(StringComparer.Ordinal);

        foreach (var pod in pods)
        {
            var key = CacheKey(pod);
            if (!string.IsNullOrEmpty(pod.Metadata?.Name))
                newCache[key] = pod;
        }

        Interlocked.Exchange(ref _cache, newCache);
    }

    private static string CacheKey(V1Pod pod)
        => $"{pod.Metadata?.NamespaceProperty ?? "default"}/{pod.Metadata?.Name}";

    private static string CacheKey(string podName, string ns = null)
        => $"{ns ?? "default"}/{podName}";
}
