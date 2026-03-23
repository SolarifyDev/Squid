using k8s;
using k8s.Models;
using Polly;
using Polly.Retry;
using Serilog;

namespace Squid.Tentacle.Kubernetes;

public class ResilientKubernetesPodOperations : IKubernetesPodOperations
{
    private readonly IKubernetesPodOperations _inner;
    private readonly RetryPolicy _retryPolicy;

    public ResilientKubernetesPodOperations(IKubernetesPodOperations inner)
    {
        _inner = inner;

        _retryPolicy = Policy
            .Handle<k8s.Autorest.HttpOperationException>(ex =>
                IsTransient(ex.Response?.StatusCode))
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
            .WaitAndRetry(
                retryCount: 5,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30)),
                onRetry: (exception, delay, attempt, _) =>
                    Log.Warning(exception, "K8s API retry {Attempt}/5 after {DelaySeconds}s", attempt, delay.TotalSeconds));
    }

    public V1Pod CreatePod(V1Pod pod, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.CreatePod(pod, namespaceParameter));

    public V1Pod ReadPodStatus(string name, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.ReadPodStatus(name, namespaceParameter));

    public Stream ReadPodLog(string name, string namespaceParameter, string container, DateTime? sinceTime = null)
        => _retryPolicy.Execute(() => _inner.ReadPodLog(name, namespaceParameter, container, sinceTime));

    public Stream ReadPodLogFollow(string name, string namespaceParameter, string container, DateTime? sinceTime = null)
        => _inner.ReadPodLogFollow(name, namespaceParameter, container, sinceTime);

    public void DeletePod(string name, string namespaceParameter, int? gracePeriodSeconds = null)
        => _retryPolicy.Execute(() => _inner.DeletePod(name, namespaceParameter, gracePeriodSeconds));

    public V1PodList ListPods(string namespaceParameter, string labelSelector)
        => _retryPolicy.Execute(() => _inner.ListPods(namespaceParameter, labelSelector));

    public Corev1EventList ListEvents(string namespaceParameter, string fieldSelector = null, string labelSelector = null)
        => _retryPolicy.Execute(() => _inner.ListEvents(namespaceParameter, fieldSelector, labelSelector));

    public V1ConfigMap CreateOrReplaceConfigMap(V1ConfigMap configMap, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.CreateOrReplaceConfigMap(configMap, namespaceParameter));

    public V1Secret CreateOrReplaceSecret(V1Secret secret, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.CreateOrReplaceSecret(secret, namespaceParameter));

    public void DeleteConfigMap(string name, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.DeleteConfigMap(name, namespaceParameter));

    public void DeleteSecret(string name, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.DeleteSecret(name, namespaceParameter));

    public V1PodDisruptionBudget? ReadPodDisruptionBudget(string name, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.ReadPodDisruptionBudget(name, namespaceParameter));

    public V1PodDisruptionBudget CreatePodDisruptionBudget(V1PodDisruptionBudget pdb, string namespaceParameter)
        => _retryPolicy.Execute(() => _inner.CreatePodDisruptionBudget(pdb, namespaceParameter));

    public V1ConfigMapList ListConfigMaps(string namespaceParameter, string labelSelector)
        => _retryPolicy.Execute(() => _inner.ListConfigMaps(namespaceParameter, labelSelector));

    public V1SecretList ListSecrets(string namespaceParameter, string labelSelector)
        => _retryPolicy.Execute(() => _inner.ListSecrets(namespaceParameter, labelSelector));

    public bool NamespaceExists(string name)
        => _retryPolicy.Execute(() => _inner.NamespaceExists(name));

    public IAsyncEnumerable<(WatchEventType, V1Pod)> WatchPodsAsync(string namespaceParameter, string labelSelector, CancellationToken ct, string resourceVersion = null)
        => _inner.WatchPodsAsync(namespaceParameter, labelSelector, ct, resourceVersion);

    public IAsyncEnumerable<(WatchEventType, Corev1Event)> WatchEventsAsync(string namespaceParameter, string fieldSelector, CancellationToken ct, string resourceVersion = null)
        => _inner.WatchEventsAsync(namespaceParameter, fieldSelector, ct, resourceVersion);

    private static bool IsTransient(System.Net.HttpStatusCode? statusCode)
    {
        if (statusCode == null) return true;

        return statusCode is System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.RequestTimeout;
    }
}
