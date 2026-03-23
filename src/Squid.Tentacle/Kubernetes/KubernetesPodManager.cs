using System.Collections.Concurrent;
using k8s.Models;
using Squid.Message.Constants;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public partial class KubernetesPodManager
{
    public const string PhaseNotFound = "NotFound";

    private readonly IKubernetesPodOperations _ops;
    private readonly KubernetesSettings _settings;
    private readonly ScriptPodTemplateProvider _templateProvider;
    private readonly PodStateCache _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _createLocks = new();

    public KubernetesPodManager(IKubernetesPodOperations ops, KubernetesSettings settings, ScriptPodTemplateProvider templateProvider = null, PodStateCache cache = null)
    {
        _ops = ops;
        _settings = settings;
        _templateProvider = templateProvider;
        _cache = cache;
    }

    private string ResolveNamespace(string? targetNamespace) => targetNamespace ?? _settings.TentacleNamespace;

    public string CreatePod(string ticketId, string? targetNamespace = null)
    {
        var semaphore = _createLocks.GetOrAdd(ticketId, _ => new SemaphoreSlim(1, 1));
        semaphore.Wait();

        try
        {
            var ns = ResolveNamespace(targetNamespace);
            var podName = $"squid-script-{ticketId[..12]}";

            var existingPod = FindPodByTicket(ticketId, targetNamespace);

            if (existingPod != null)
            {
                var phase = GetPodPhase(existingPod, targetNamespace);

                if (phase is "Succeeded" or "Failed")
                {
                    Log.Information("Pod {PodName} is terminal ({Phase}), recreating for ticket {TicketId}", existingPod, phase, ticketId);
                    DeletePod(existingPod, targetNamespace: targetNamespace);
                }
                else
                {
                    Log.Information("Reusing existing pod {PodName} (phase: {Phase}) for ticket {TicketId}", existingPod, phase, ticketId);
                    return existingPod;
                }
            }

            var pod = BuildPodSpec(podName, ticketId);
            _ops.CreatePod(pod, ns);

            Log.Information("Created script pod {PodName} for ticket {TicketId} in namespace {Namespace}", podName, ticketId, ns);

            return podName;
        }
        finally
        {
            semaphore.Release();
            _createLocks.TryRemove(ticketId, out _);
        }
    }

    public string? FindPodByTicket(string ticketId, string? targetNamespace = null)
    {
        if (_cache != null && _cache.TryGetPodByTicket(ticketId, out var cached))
            return cached.Metadata?.Name;

        try
        {
            var ns = ResolveNamespace(targetNamespace);
            var labelSelector = $"squid.io/ticket-id={ticketId}";
            var pods = _ops.ListPods(ns, labelSelector);

            var existing = pods?.Items?.FirstOrDefault();

            return existing?.Metadata?.Name;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to search for existing pod with ticket {TicketId}", ticketId);
            return null;
        }
    }

    public string? GetPodPhase(string podName, string? targetNamespace = null)
    {
        var ns = ResolveNamespace(targetNamespace);

        if (_cache != null && _cache.TryGetPod(podName, out var cached, ns))
            return cached.Status?.Phase;

        try
        {
            var pod = _ops.ReadPodStatus(podName, ns);
            return pod?.Status?.Phase;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warning("Pod {PodName} not found", podName);
            return PhaseNotFound;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read phase for pod {PodName}, treating as Running", podName);
            return null;
        }
    }

    public int GetPodExitCode(string podName, string? targetNamespace = null)
    {
        try
        {
            var ns = ResolveNamespace(targetNamespace);
            var pod = _ops.ReadPodStatus(podName, ns);

            var containerStatus = pod?.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "script");

            if (containerStatus?.State?.Terminated == null)
            {
                Log.Warning("Pod {PodName} container 'script' has no Terminated state", podName);
                return ScriptExitCodes.PodNotFound;
            }

            return containerStatus.State.Terminated.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read exit code for pod {PodName}", podName);
            return ScriptExitCodes.PodNotFound;
        }
    }

    public string ReadPodLogs(string podName, DateTime? sinceTime = null, string? targetNamespace = null)
    {
        try
        {
            var ns = ResolveNamespace(targetNamespace);
            using var stream = _ops.ReadPodLog(podName, ns, "script", sinceTime);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.BadRequest && ex.Response.Content?.Contains("PodInitializing") == true)
        {
            Log.Debug("Pod {PodName} container not ready yet (PodInitializing)", podName);
            return string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read logs for pod {PodName}", podName);
            return string.Empty;
        }
    }

    public void DeletePod(string podName, int? gracePeriodSeconds = null, string? targetNamespace = null)
    {
        try
        {
            var ns = ResolveNamespace(targetNamespace);
            _ops.DeletePod(podName, ns, gracePeriodSeconds);

            Log.Information("Deleted script pod {PodName} (gracePeriod={GracePeriod})", podName, gracePeriodSeconds?.ToString() ?? "default");
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Debug("Pod {PodName} already deleted", podName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete pod {PodName}", podName);
        }
    }

    public async Task WaitForPodTerminationAsync(string podName, TimeSpan timeout, string? targetNamespace = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var phase = GetPodPhase(podName, targetNamespace);

                if (phase is "Succeeded" or "Failed" or PhaseNotFound)
                    return;

                await Task.Delay(1000, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout expired, not caller cancellation
        }

        Log.Warning("Pod {PodName} did not terminate within {TimeoutSeconds}s", podName, timeout.TotalSeconds);
    }

    public void WaitForPodTermination(string podName, TimeSpan timeout, string? targetNamespace = null)
        => WaitForPodTerminationAsync(podName, timeout, targetNamespace).GetAwaiter().GetResult();

    public bool NamespaceExists(string ns) => _ops.NamespaceExists(ns);

    public List<V1Pod> ListManagedPods()
    {
        var labelSelector = "app.kubernetes.io/managed-by=kubernetes-agent";

        if (!string.IsNullOrEmpty(_settings.ReleaseName))
            labelSelector += $",app.kubernetes.io/instance={_settings.ReleaseName}";

        var pods = _ops.ListPods(_settings.TentacleNamespace, labelSelector);

        return pods.Items.ToList();
    }
}
