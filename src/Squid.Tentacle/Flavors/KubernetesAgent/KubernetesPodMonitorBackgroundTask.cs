using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Flavors.KubernetesAgent;

public sealed class KubernetesPodMonitorBackgroundTask : ITentacleBackgroundTask
{
    private readonly KubernetesPodMonitor _monitor;

    public KubernetesPodMonitorBackgroundTask(KubernetesPodMonitor monitor)
    {
        _monitor = monitor;
    }

    public string Name => "KubernetesPodMonitor";

    public Task RunAsync(CancellationToken ct) => _monitor.RunAsync(ct);
}
