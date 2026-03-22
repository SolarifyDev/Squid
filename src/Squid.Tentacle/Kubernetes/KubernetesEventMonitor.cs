using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.ScriptExecution;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public sealed class KubernetesEventMonitor : ITentacleBackgroundTask
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> WarningReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "FailedScheduling",
        "BackOff",
        "OOMKilled",
        "ErrImagePull",
        "ImagePullBackOff",
        "FailedMount",
        "FailedAttachVolume",
        "Evicted",
        "Preempting"
    };

    private readonly IKubernetesPodOperations _podOps;
    private readonly KubernetesSettings _settings;
    private readonly ScriptPodService _scriptPodService;
    private DateTime _lastEventTime = DateTime.UtcNow;

    public KubernetesEventMonitor(IKubernetesPodOperations podOps, KubernetesSettings settings, ScriptPodService scriptPodService = null)
    {
        _podOps = podOps;
        _settings = settings;
        _scriptPodService = scriptPodService;
    }

    public string Name => "KubernetesEventMonitor";

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Information("Kubernetes event monitor started for namespace {Namespace}", _settings.TentacleNamespace);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                PollEvents();
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Event monitor poll failed");
            }
        }
    }

    internal void PollEvents()
    {
        var events = _podOps.ListEvents(
            _settings.TentacleNamespace,
            fieldSelector: "involvedObject.kind=Pod");

        if (events?.Items == null) return;

        foreach (var evt in events.Items)
        {
            var eventTime = evt.LastTimestamp ?? evt.EventTime;

            if (eventTime == null || eventTime <= _lastEventTime) continue;

            if (!IsManagedPod(evt.InvolvedObject?.Name)) continue;

            if (WarningReasons.Contains(evt.Reason ?? ""))
            {
                Log.Warning("K8s event: {Reason} on pod {PodName} — {Message}", evt.Reason, evt.InvolvedObject?.Name, evt.Message);

                InjectEventIntoScript(evt.InvolvedObject.Name, evt.Reason, evt.Message);
            }
        }

        var latestTime = events.Items
            .Select(e => e.LastTimestamp ?? e.EventTime)
            .Where(t => t.HasValue)
            .Select(t => t.Value)
            .DefaultIfEmpty(_lastEventTime)
            .Max();

        _lastEventTime = latestTime;
    }

    private void InjectEventIntoScript(string podName, string reason, string message)
    {
        if (_scriptPodService == null) return;

        foreach (var kvp in _scriptPodService.ActiveScripts)
        {
            if (!string.Equals(kvp.Value.PodName, podName, StringComparison.Ordinal)) continue;

            kvp.Value.InjectedEvents.Enqueue(new ProcessOutput(ProcessOutputSource.StdErr, $"[K8s Event] {reason}: {message}"));
            return;
        }
    }

    private static bool IsManagedPod(string podName)
    {
        if (string.IsNullOrEmpty(podName)) return false;

        return podName.StartsWith("squid-script-", StringComparison.OrdinalIgnoreCase);
    }
}
