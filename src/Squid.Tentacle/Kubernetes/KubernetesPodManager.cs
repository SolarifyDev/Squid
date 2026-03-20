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

    public KubernetesPodManager(IKubernetesPodOperations ops, KubernetesSettings settings, ScriptPodTemplateProvider templateProvider = null)
    {
        _ops = ops;
        _settings = settings;
        _templateProvider = templateProvider;
    }

    public string CreatePod(string ticketId)
    {
        var podName = $"squid-script-{ticketId[..12]}";

        var existingPod = FindPodByTicket(ticketId);

        if (existingPod != null)
        {
            Log.Information("Reusing existing pod {PodName} for ticket {TicketId}", existingPod, ticketId);
            return existingPod;
        }

        var pod = BuildPodSpec(podName, ticketId);
        _ops.CreatePod(pod, _settings.TentacleNamespace);

        Log.Information("Created script pod {PodName} for ticket {TicketId}", podName, ticketId);

        return podName;
    }

    public string? FindPodByTicket(string ticketId)
    {
        try
        {
            var labelSelector = $"squid.io/ticket-id={ticketId}";
            var pods = _ops.ListPods(_settings.TentacleNamespace, labelSelector);

            var existing = pods?.Items?.FirstOrDefault();

            return existing?.Metadata?.Name;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to search for existing pod with ticket {TicketId}", ticketId);
            return null;
        }
    }

    public string? GetPodPhase(string podName)
    {
        try
        {
            var pod = _ops.ReadPodStatus(podName, _settings.TentacleNamespace);
            return pod?.Status?.Phase;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warning("Pod {PodName} not found", podName);
            return PhaseNotFound;
        }
    }

    public int GetPodExitCode(string podName)
    {
        try
        {
            var pod = _ops.ReadPodStatus(podName, _settings.TentacleNamespace);

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

    public string ReadPodLogs(string podName)
    {
        try
        {
            var stream = _ops.ReadPodLog(podName, _settings.TentacleNamespace, "script");

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

    public void DeletePod(string podName)
    {
        try
        {
            _ops.DeletePod(podName, _settings.TentacleNamespace);

            Log.Information("Deleted script pod {PodName}", podName);
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

    public void WaitForPodTermination(string podName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var phase = GetPodPhase(podName);

            if (phase is "Succeeded" or "Failed" or PhaseNotFound)
                return;

            Thread.Sleep(1000);
        }

        Log.Warning("Pod {PodName} did not terminate within {TimeoutSeconds}s", podName, timeout.TotalSeconds);
    }

    public List<V1Pod> ListManagedPods()
    {
        var labelSelector = "app.kubernetes.io/managed-by=kubernetes-agent";

        if (!string.IsNullOrEmpty(_settings.ReleaseName))
            labelSelector += $",app.kubernetes.io/instance={_settings.ReleaseName}";

        var pods = _ops.ListPods(_settings.TentacleNamespace, labelSelector);

        return pods.Items.ToList();
    }
}
