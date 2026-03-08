using System.Text;
using k8s.Models;
using Serilog;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Kubernetes;

public class KubernetesResourceManager
{
    private const string ManagedByLabel = "kubernetes-agent";
    private const string ManagedByKey = "app.kubernetes.io/managed-by";

    private readonly IKubernetesPodOperations _podOps;
    private readonly KubernetesSettings _settings;

    public KubernetesResourceManager(IKubernetesPodOperations podOps, KubernetesSettings settings)
    {
        _podOps = podOps;
        _settings = settings;
    }

    public V1ConfigMap CreateOrUpdateConfigMap(string name, Dictionary<string, string> data)
    {
        var configMap = new V1ConfigMap
        {
            Metadata = BuildMetadata(name),
            Data = data
        };

        var result = _podOps.CreateOrReplaceConfigMap(configMap, _settings.TentacleNamespace);

        Log.Debug("ConfigMap {Name} created/updated in {Namespace}", name, _settings.TentacleNamespace);

        return result;
    }

    public V1Secret CreateOrUpdateSecret(string name, Dictionary<string, string> data)
    {
        var binaryData = data.ToDictionary(
            kvp => kvp.Key,
            kvp => Encoding.UTF8.GetBytes(kvp.Value));

        var secret = new V1Secret
        {
            Metadata = BuildMetadata(name),
            Data = binaryData,
            Type = "Opaque"
        };

        var result = _podOps.CreateOrReplaceSecret(secret, _settings.TentacleNamespace);

        Log.Debug("Secret {Name} created/updated in {Namespace}", name, _settings.TentacleNamespace);

        return result;
    }

    public void DeleteConfigMap(string name)
    {
        try
        {
            _podOps.DeleteConfigMap(name, _settings.TentacleNamespace);
            Log.Debug("ConfigMap {Name} deleted from {Namespace}", name, _settings.TentacleNamespace);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete ConfigMap {Name}", name);
        }
    }

    public void DeleteSecret(string name)
    {
        try
        {
            _podOps.DeleteSecret(name, _settings.TentacleNamespace);
            Log.Debug("Secret {Name} deleted from {Namespace}", name, _settings.TentacleNamespace);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete Secret {Name}", name);
        }
    }

    private static V1ObjectMeta BuildMetadata(string name)
    {
        return new V1ObjectMeta
        {
            Name = name,
            Labels = new Dictionary<string, string>
            {
                [ManagedByKey] = ManagedByLabel
            }
        };
    }
}
