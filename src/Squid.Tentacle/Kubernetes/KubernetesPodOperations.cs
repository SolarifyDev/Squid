using System.Runtime.CompilerServices;
using k8s;
using k8s.Models;

namespace Squid.Tentacle.Kubernetes;

public class KubernetesPodOperations : IKubernetesPodOperations
{
    private readonly IKubernetes _client;

    public KubernetesPodOperations(IKubernetes client)
    {
        _client = client;
    }

    public V1Pod CreatePod(V1Pod pod, string namespaceParameter)
        => _client.CoreV1.CreateNamespacedPod(pod, namespaceParameter);

    public V1Pod ReadPodStatus(string name, string namespaceParameter)
        => _client.CoreV1.ReadNamespacedPodStatus(name, namespaceParameter);

    public Stream ReadPodLog(string name, string namespaceParameter, string container, DateTime? sinceTime = null)
    {
        int? sinceSeconds = null;

        if (sinceTime.HasValue)
        {
            var elapsed = (int)(DateTime.UtcNow - sinceTime.Value).TotalSeconds;
            sinceSeconds = Math.Max(elapsed, 1);
        }

        return _client.CoreV1.ReadNamespacedPodLog(name, namespaceParameter, container: container, sinceSeconds: sinceSeconds);
    }

    public void DeletePod(string name, string namespaceParameter, int? gracePeriodSeconds = null)
        => _client.CoreV1.DeleteNamespacedPod(name, namespaceParameter, gracePeriodSeconds: gracePeriodSeconds);

    public V1PodList ListPods(string namespaceParameter, string labelSelector)
        => _client.CoreV1.ListNamespacedPod(namespaceParameter, labelSelector: labelSelector);

    public Corev1EventList ListEvents(string namespaceParameter, string fieldSelector = null, string labelSelector = null)
        => _client.CoreV1.ListNamespacedEvent(namespaceParameter, fieldSelector: fieldSelector, labelSelector: labelSelector);

    public V1ConfigMap CreateOrReplaceConfigMap(V1ConfigMap configMap, string namespaceParameter)
    {
        try
        {
            return _client.CoreV1.ReplaceNamespacedConfigMap(configMap, configMap.Metadata.Name, namespaceParameter);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return _client.CoreV1.CreateNamespacedConfigMap(configMap, namespaceParameter);
        }
    }

    public V1Secret CreateOrReplaceSecret(V1Secret secret, string namespaceParameter)
    {
        try
        {
            return _client.CoreV1.ReplaceNamespacedSecret(secret, secret.Metadata.Name, namespaceParameter);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return _client.CoreV1.CreateNamespacedSecret(secret, namespaceParameter);
        }
    }

    public void DeleteConfigMap(string name, string namespaceParameter)
        => _client.CoreV1.DeleteNamespacedConfigMap(name, namespaceParameter);

    public void DeleteSecret(string name, string namespaceParameter)
        => _client.CoreV1.DeleteNamespacedSecret(name, namespaceParameter);

    public V1PodDisruptionBudget? ReadPodDisruptionBudget(string name, string namespaceParameter)
    {
        try
        {
            return _client.PolicyV1.ReadNamespacedPodDisruptionBudget(name, namespaceParameter);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public V1PodDisruptionBudget CreatePodDisruptionBudget(V1PodDisruptionBudget pdb, string namespaceParameter)
        => _client.PolicyV1.CreateNamespacedPodDisruptionBudget(pdb, namespaceParameter);

    public V1ConfigMapList ListConfigMaps(string namespaceParameter, string labelSelector)
        => _client.CoreV1.ListNamespacedConfigMap(namespaceParameter, labelSelector: labelSelector);

    public bool NamespaceExists(string name)
    {
        try
        {
            _client.CoreV1.ReadNamespace(name);
            return true;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<(WatchEventType, V1Pod)> WatchPodsAsync(string namespaceParameter, string labelSelector, [EnumeratorCancellation] CancellationToken ct)
    {
        var response = _client.CoreV1.ListNamespacedPodWithHttpMessagesAsync(namespaceParameter, labelSelector: labelSelector, watch: true, cancellationToken: ct);

        await foreach (var (type, pod) in response.WatchAsync<V1Pod, V1PodList>(cancellationToken: ct).ConfigureAwait(false))
        {
            yield return (type, pod);
        }
    }
}
