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
    private readonly ImagePullSecretManager _pullSecretManager;
    private readonly PodStateCache _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _createLocks = new();

    public KubernetesPodManager(IKubernetesPodOperations ops, KubernetesSettings settings, ScriptPodTemplateProvider templateProvider = null, ImagePullSecretManager pullSecretManager = null, PodStateCache cache = null)
    {
        _ops = ops;
        _settings = settings;
        _templateProvider = templateProvider;
        _pullSecretManager = pullSecretManager;
        _cache = cache;
    }

    private string ResolveNamespace(string? targetNamespace) => targetNamespace ?? _settings.TentacleNamespace;

    public string CreatePod(string ticketId, string? targetNamespace = null, Dictionary<string, string>? additionalLabels = null)
    {
        var semaphore = _createLocks.GetOrAdd(ticketId, _ => new SemaphoreSlim(1, 1));

        if (!semaphore.Wait(TimeSpan.FromSeconds(60)))
            throw new TimeoutException($"Timed out waiting 60s for pod creation lock for ticket {ticketId}");

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

            var pod = BuildPodSpec(podName, ticketId, ns, additionalLabels);
            _ops.CreatePod(pod, ns);

            Log.Information("Created script pod {PodName} for ticket {TicketId} in namespace {Namespace}", podName, ticketId, ns);

            return podName;
        }
        finally
        {
            semaphore.Release();
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

    public ContainerTerminationResult? GetScriptContainerTermination(string podName, string? targetNamespace = null)
    {
        var ns = ResolveNamespace(targetNamespace);
        V1Pod pod;

        if (_cache != null && _cache.TryGetPod(podName, out var cached, ns))
            pod = cached;
        else
        {
            try { pod = _ops.ReadPodStatus(podName, ns); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read container state for pod {PodName}", podName);
                return null;
            }
        }

        var status = pod?.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "script");
        if (status?.State?.Terminated == null) return null;

        return new ContainerTerminationResult(status.State.Terminated.ExitCode, status.State.Terminated.Reason, status.State.Terminated.Message, status.State.Terminated.Signal);
    }

    public string? GetContainerDiagnostics(string podName, string? targetNamespace = null)
    {
        try
        {
            var ns = ResolveNamespace(targetNamespace);
            V1Pod pod;

            if (_cache != null && _cache.TryGetPod(podName, out var cached, ns))
                pod = cached;
            else
                pod = _ops.ReadPodStatus(podName, ns);

            var status = pod?.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "script");
            if (status?.State?.Terminated == null) return null;

            var terminated = status.State.Terminated;
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(terminated.Reason))
                parts.Add($"Reason: {terminated.Reason}");
            if (!string.IsNullOrEmpty(terminated.Message))
                parts.Add($"Message: {terminated.Message}");
            if (terminated.Signal.HasValue)
                parts.Add($"Signal: {terminated.Signal}");

            return parts.Count > 0
                ? $"Container 'script' terminated — {string.Join(", ", parts)}"
                : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read container diagnostics for pod {PodName}", podName);
            return null;
        }
    }

    public PodStartupDiagnostic? GetPodStartupDiagnostics(string podName, string? targetNamespace = null)
    {
        try
        {
            var ns = ResolveNamespace(targetNamespace);
            V1Pod pod;

            if (_cache != null && _cache.TryGetPod(podName, out var cached, ns))
                pod = cached;
            else
                pod = _ops.ReadPodStatus(podName, ns);

            if (pod?.Status == null) return null;

            var initDiag = CheckInitContainerWaiting(pod);
            if (initDiag != null) return initDiag;

            var scriptDiag = CheckScriptContainerWaiting(pod);
            if (scriptDiag != null) return scriptDiag;

            return CheckUnschedulable(pod);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read startup diagnostics for pod {PodName}", podName);
            return null;
        }
    }

    private static PodStartupDiagnostic? CheckInitContainerWaiting(V1Pod pod)
    {
        if (pod.Status.InitContainerStatuses == null) return null;

        foreach (var init in pod.Status.InitContainerStatuses)
        {
            var waiting = init.State?.Waiting;
            if (waiting?.Reason == null) continue;

            return new PodStartupDiagnostic(
                waiting.Reason,
                $"Init container '{init.Name}' — {waiting.Reason}: {waiting.Message ?? "no details"}",
                IsPermanentReason(waiting.Reason));
        }

        return null;
    }

    private static PodStartupDiagnostic? CheckScriptContainerWaiting(V1Pod pod)
    {
        var script = pod.Status.ContainerStatuses?.FirstOrDefault(c => c.Name == "script");
        var waiting = script?.State?.Waiting;

        if (waiting?.Reason == null) return null;

        return new PodStartupDiagnostic(
            waiting.Reason,
            $"Container 'script' — {waiting.Reason}: {waiting.Message ?? "no details"}",
            IsPermanentReason(waiting.Reason));
    }

    private static PodStartupDiagnostic? CheckUnschedulable(V1Pod pod)
    {
        var condition = pod.Status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
        if (condition == null) return null;

        return new PodStartupDiagnostic(
            condition.Reason ?? "Unschedulable",
            $"Pod scheduling failed — {condition.Reason ?? "Unschedulable"}: {condition.Message ?? "no details"}",
            IsPermanentReason(condition.Reason ?? "Unschedulable"));
    }

    private static bool IsPermanentReason(string reason)
    {
        return reason is "CreateContainerConfigError" or "InvalidImageName" or "RunContainerError" or "CreateContainerError";
    }

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

public record ContainerTerminationResult(int ExitCode, string? Reason, string? Message = null, long? Signal = null);

public record PodStartupDiagnostic(string Reason, string Message, bool IsPermanent);
