using k8s;
using k8s.Models;

namespace Squid.Tentacle.Kubernetes;

public interface IKubernetesPodOperations
{
    V1Pod CreatePod(V1Pod pod, string namespaceParameter);
    V1Pod ReadPodStatus(string name, string namespaceParameter);
    Stream ReadPodLog(string name, string namespaceParameter, string container, DateTime? sinceTime = null);
    Stream ReadPodLogFollow(string name, string namespaceParameter, string container, DateTime? sinceTime = null);
    void DeletePod(string name, string namespaceParameter, int? gracePeriodSeconds = null);
    V1PodList ListPods(string namespaceParameter, string labelSelector);
    Corev1EventList ListEvents(string namespaceParameter, string fieldSelector = null, string labelSelector = null);
    V1ConfigMap CreateOrReplaceConfigMap(V1ConfigMap configMap, string namespaceParameter);
    V1Secret CreateOrReplaceSecret(V1Secret secret, string namespaceParameter);
    void DeleteConfigMap(string name, string namespaceParameter);
    void DeleteSecret(string name, string namespaceParameter);
    IAsyncEnumerable<(WatchEventType, V1Pod)> WatchPodsAsync(string namespaceParameter, string labelSelector, CancellationToken ct, string resourceVersion = null);
    IAsyncEnumerable<(WatchEventType, Corev1Event)> WatchEventsAsync(string namespaceParameter, string fieldSelector, CancellationToken ct, string resourceVersion = null);
    V1PodDisruptionBudget? ReadPodDisruptionBudget(string name, string namespaceParameter);
    V1PodDisruptionBudget CreatePodDisruptionBudget(V1PodDisruptionBudget pdb, string namespaceParameter);
    V1ConfigMapList ListConfigMaps(string namespaceParameter, string labelSelector);
    V1SecretList ListSecrets(string namespaceParameter, string labelSelector);
    bool NamespaceExists(string name);
}
