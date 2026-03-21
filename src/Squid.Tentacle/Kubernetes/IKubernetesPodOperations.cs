using k8s;
using k8s.Models;

namespace Squid.Tentacle.Kubernetes;

public interface IKubernetesPodOperations
{
    V1Pod CreatePod(V1Pod pod, string namespaceParameter);
    V1Pod ReadPodStatus(string name, string namespaceParameter);
    Stream ReadPodLog(string name, string namespaceParameter, string container);
    void DeletePod(string name, string namespaceParameter);
    V1PodList ListPods(string namespaceParameter, string labelSelector);
    Corev1EventList ListEvents(string namespaceParameter, string fieldSelector = null, string labelSelector = null);
    V1ConfigMap CreateOrReplaceConfigMap(V1ConfigMap configMap, string namespaceParameter);
    V1Secret CreateOrReplaceSecret(V1Secret secret, string namespaceParameter);
    void DeleteConfigMap(string name, string namespaceParameter);
    void DeleteSecret(string name, string namespaceParameter);
    IAsyncEnumerable<(WatchEventType, V1Pod)> WatchPodsAsync(string namespaceParameter, string labelSelector, CancellationToken ct);
}
