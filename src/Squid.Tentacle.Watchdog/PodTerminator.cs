using k8s;
using k8s.Models;

namespace Squid.Tentacle.Watchdog;

public interface IPodTerminator
{
    Task TerminateAsync(CancellationToken ct);
}

public sealed class PodTerminator : IPodTerminator
{
    private readonly IKubernetes _client;
    private readonly string _podName;
    private readonly string _podNamespace;

    public PodTerminator(IKubernetes client, string podName, string podNamespace)
    {
        _client = client;
        _podName = podName;
        _podNamespace = podNamespace;
    }

    public async Task TerminateAsync(CancellationToken ct)
    {
        RaiseEvent();

        await _client.CoreV1.DeleteNamespacedPodAsync(
            _podName, _podNamespace,
            propagationPolicy: "Foreground",
            cancellationToken: ct).ConfigureAwait(false);

        Console.WriteLine($"Pod {_podName} deleted. Exiting.");
    }

    // Best effort: create K8s Event before deletion
    private void RaiseEvent()
    {
        try
        {
            var evt = new Eventsv1Event
            {
                Metadata = new V1ObjectMeta
                {
                    GenerateName = "nfs-watchdog-",
                    NamespaceProperty = _podNamespace
                },
                Action = "NfsWatchdogTimeout",
                Reason = "NfsWatchdogTimeout",
                Note = "Stale NFS mount detected, deleting pod",
                Type = "Warning",
                ReportingController = "squid-nfs-watchdog",
                ReportingInstance = _podName,
                Regarding = new V1ObjectReference
                {
                    Kind = "Pod",
                    Name = _podName,
                    NamespaceProperty = _podNamespace
                },
                EventTime = DateTime.UtcNow
            };

            _client.EventsV1.CreateNamespacedEvent(evt, _podNamespace);
            Console.WriteLine("K8s event raised: NfsWatchdogTimeout");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to raise K8s event: {ex.Message}");
        }
    }
}
