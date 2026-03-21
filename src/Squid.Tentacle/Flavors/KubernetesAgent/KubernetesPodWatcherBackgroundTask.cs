using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Flavors.KubernetesAgent;

public sealed class KubernetesPodWatcherBackgroundTask : ITentacleBackgroundTask
{
    private readonly KubernetesPodWatcher _watcher;

    public KubernetesPodWatcherBackgroundTask(KubernetesPodWatcher watcher)
    {
        _watcher = watcher;
    }

    public string Name => "KubernetesPodWatcher";

    public Task RunAsync(CancellationToken ct) => _watcher.RunAsync(ct);
}
