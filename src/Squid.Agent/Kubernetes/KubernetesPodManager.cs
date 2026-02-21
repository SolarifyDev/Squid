using k8s;
using k8s.Models;
using Squid.Agent.Configuration;
using Serilog;

namespace Squid.Agent.Kubernetes;

public partial class KubernetesPodManager
{
    private readonly IKubernetes _client;
    private readonly AgentSettings _settings;

    public KubernetesPodManager(IKubernetes client, AgentSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public string CreatePod(string ticketId)
    {
        var podName = $"squid-script-{ticketId[..12]}";
        var pod = BuildPodSpec(podName, ticketId);

        _client.CoreV1.CreateNamespacedPod(pod, _settings.AgentNamespace);

        Log.Information("Created script pod {PodName} for ticket {TicketId}", podName, ticketId);

        return podName;
    }

    public string? GetPodPhase(string podName)
    {
        try
        {
            var pod = _client.CoreV1.ReadNamespacedPodStatus(podName, _settings.AgentNamespace);
            return pod?.Status?.Phase;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Warning("Pod {PodName} not found", podName);
            return null;
        }
    }

    public int GetPodExitCode(string podName)
    {
        try
        {
            var pod = _client.CoreV1.ReadNamespacedPodStatus(podName, _settings.AgentNamespace);

            var containerStatus = pod?.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "script");

            return containerStatus?.State?.Terminated?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    public string ReadPodLogs(string podName)
    {
        try
        {
            var stream = _client.CoreV1.ReadNamespacedPodLog(
                podName, _settings.AgentNamespace, container: "script");

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    public void DeletePod(string podName)
    {
        try
        {
            _client.CoreV1.DeleteNamespacedPod(podName, _settings.AgentNamespace);

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

            if (phase is "Succeeded" or "Failed" or null)
                return;

            Thread.Sleep(1000);
        }

        Log.Warning("Pod {PodName} did not terminate within {TimeoutSeconds}s", podName, timeout.TotalSeconds);
    }

    public List<V1Pod> ListManagedPods()
    {
        var pods = _client.CoreV1.ListNamespacedPod(
            _settings.AgentNamespace,
            labelSelector: "app.kubernetes.io/managed-by=squid-agent");

        return pods.Items.ToList();
    }
}
