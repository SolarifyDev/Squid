using System.Text.Json;
using k8s;
using Serilog;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Kubernetes;

public class ScriptPodTemplateProvider
{
    private const string CrdGroup = "squid.io";
    private const string CrdVersion = "v1alpha1";
    private const string CrdPlural = "scriptpodtemplates";
    private const string DefaultTemplateName = "default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IKubernetes _client;
    private readonly KubernetesSettings _settings;

    public ScriptPodTemplateProvider(IKubernetes client, KubernetesSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public virtual ScriptPodTemplate TryLoadTemplate(string templateName = DefaultTemplateName)
    {
        try
        {
            var result = _client.CustomObjects.GetNamespacedCustomObject(
                CrdGroup, CrdVersion, _settings.TentacleNamespace, CrdPlural, templateName);

            var json = JsonSerializer.Serialize(result, JsonOptions);
            var wrapper = JsonSerializer.Deserialize<CrdWrapper>(json, JsonOptions);

            return wrapper?.Spec;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Log.Debug("No ScriptPodTemplate CRD found in {Namespace}", _settings.TentacleNamespace);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load ScriptPodTemplate CRD");
            return null;
        }
    }

    private sealed class CrdWrapper
    {
        public ScriptPodTemplate Spec { get; set; }
    }
}
