using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.ScriptExecution;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public sealed class KubernetesEventMonitor : ITentacleBackgroundTask
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> DefaultWarningReasons = new(StringComparer.OrdinalIgnoreCase)
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
    private readonly HashSet<string> _warningReasons;
    private DateTime _lastEventTime = DateTime.UtcNow;
    private string _lastResourceVersion;

    public KubernetesEventMonitor(IKubernetesPodOperations podOps, KubernetesSettings settings, ScriptPodService scriptPodService = null)
    {
        _podOps = podOps;
        _settings = settings;
        _scriptPodService = scriptPodService;
        _warningReasons = BuildWarningReasons(settings.AdditionalWarningReasons);
    }

    internal int WarningReasonCount => _warningReasons.Count;

    private static HashSet<string> BuildWarningReasons(string additionalReasons)
    {
        var reasons = new HashSet<string>(DefaultWarningReasons, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(additionalReasons)) return reasons;

        foreach (var reason in additionalReasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            reasons.Add(reason);
        }

        return reasons;
    }

    public string Name => "KubernetesEventMonitor";

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Information("Kubernetes event monitor started for namespace {Namespace}", _settings.TentacleNamespace);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WatchEventsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Event watch stream disconnected, falling back to poll then reconnecting");

                try { PollEvents(); }
                catch (Exception pollEx) { Log.Debug(pollEx, "Fallback poll also failed"); }

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

    internal async Task WatchEventsAsync(CancellationToken ct)
    {
        await foreach (var (eventType, evt) in _podOps.WatchEventsAsync(_settings.TentacleNamespace, "involvedObject.kind=Pod", ct, _lastResourceVersion).ConfigureAwait(false))
        {
            _lastResourceVersion = evt.Metadata?.ResourceVersion ?? _lastResourceVersion;

            var eventTime = evt.LastTimestamp ?? evt.EventTime;
            if (eventTime.HasValue && eventTime > _lastEventTime)
                _lastEventTime = eventTime.Value;

            if (!IsManagedPod(evt.InvolvedObject?.Name)) continue;
            if (!_warningReasons.Contains(evt.Reason ?? "")) continue;

            Log.Warning("K8s event: {Reason} on pod {PodName} — {Message}", evt.Reason, evt.InvolvedObject?.Name, evt.Message);
            InjectEventIntoScript(evt.InvolvedObject.Name, evt.Reason, evt.Message);
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
            var eventRv = evt.Metadata?.ResourceVersion;
            if (eventRv != null && string.Compare(eventRv, _lastResourceVersion, StringComparison.Ordinal) <= 0) continue;

            var eventTime = evt.LastTimestamp ?? evt.EventTime;
            if (eventTime == null || eventTime <= _lastEventTime) continue;

            if (!IsManagedPod(evt.InvolvedObject?.Name)) continue;

            if (_warningReasons.Contains(evt.Reason ?? ""))
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

        var latestRv = events.Items
            .Select(e => e.Metadata?.ResourceVersion)
            .Where(rv => rv != null)
            .DefaultIfEmpty(_lastResourceVersion)
            .Max(StringComparer.Ordinal);

        _lastResourceVersion = latestRv;
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
