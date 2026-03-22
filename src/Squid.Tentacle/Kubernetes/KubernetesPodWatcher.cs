using k8s;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public sealed class KubernetesPodWatcher
{
    private readonly IKubernetesPodOperations _podOps;
    private readonly KubernetesSettings _settings;
    private readonly PodStateCache _cache;

    public KubernetesPodWatcher(IKubernetesPodOperations podOps, KubernetesSettings settings, PodStateCache cache = null)
    {
        _podOps = podOps;
        _settings = settings;
        _cache = cache;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var labelSelector = "app.kubernetes.io/managed-by=kubernetes-agent";

        if (!string.IsNullOrEmpty(_settings.ReleaseName))
            labelSelector += $",app.kubernetes.io/instance={_settings.ReleaseName}";

        Log.Information("Pod watcher started. Label selector: {LabelSelector}", labelSelector);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                PopulateCache(labelSelector);

                await foreach (var (eventType, pod) in _podOps.WatchPodsAsync(_settings.TentacleNamespace, labelSelector, ct).ConfigureAwait(false))
                {
                    _cache?.HandleWatchEvent(eventType, pod);

                    if (eventType is WatchEventType.Modified or WatchEventType.Deleted)
                        HandlePodEvent(eventType, pod);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pod watch stream disconnected, reconnecting...");
                _cache?.Invalidate();

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void PopulateCache(string labelSelector)
    {
        if (_cache == null) return;

        try
        {
            var pods = _podOps.ListPods(_settings.TentacleNamespace, labelSelector);
            _cache.Populate(pods.Items);

            Log.Debug("Cache populated with {Count} pods", pods.Items.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to populate pod cache");
        }
    }

    internal static void HandlePodEvent(WatchEventType eventType, k8s.Models.V1Pod pod)
    {
        var phase = pod.Status?.Phase;

        string ticketId = null;
        pod.Metadata?.Labels?.TryGetValue("squid.io/ticket-id", out ticketId);

        if (ticketId == null) return;
        if (phase is not ("Succeeded" or "Failed")) return;

        Log.Information("Watch: Pod {PodName} reached {Phase} for ticket {TicketId}", pod.Metadata.Name, phase, ticketId);
    }
}
