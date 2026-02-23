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

    public Stream ReadPodLog(string name, string namespaceParameter, string container)
        => _client.CoreV1.ReadNamespacedPodLog(name, namespaceParameter, container: container);

    public void DeletePod(string name, string namespaceParameter)
        => _client.CoreV1.DeleteNamespacedPod(name, namespaceParameter);

    public V1PodList ListPods(string namespaceParameter, string labelSelector)
        => _client.CoreV1.ListNamespacedPod(namespaceParameter, labelSelector: labelSelector);
}
