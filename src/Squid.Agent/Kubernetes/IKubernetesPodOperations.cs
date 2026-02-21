using k8s.Models;

namespace Squid.Agent.Kubernetes;

public interface IKubernetesPodOperations
{
    V1Pod CreatePod(V1Pod pod, string namespaceParameter);
    V1Pod ReadPodStatus(string name, string namespaceParameter);
    Stream ReadPodLog(string name, string namespaceParameter, string container);
    void DeletePod(string name, string namespaceParameter);
    V1PodList ListPods(string namespaceParameter, string labelSelector);
}
