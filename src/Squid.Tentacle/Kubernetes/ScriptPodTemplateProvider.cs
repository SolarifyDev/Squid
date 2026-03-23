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

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly IKubernetes _client;
    private readonly KubernetesSettings _settings;
    private readonly IKubernetesPodOperations _ops;
    private ScriptPodTemplate? _cachedTemplate;
    private DateTime _cacheExpiry = DateTime.MinValue;

    public ScriptPodTemplateProvider(IKubernetes client, KubernetesSettings settings, IKubernetesPodOperations ops = null)
    {
        _client = client;
        _settings = settings;
        _ops = ops;
    }

    public virtual ScriptPodTemplate TryLoadTemplate(string templateName = DefaultTemplateName)
    {
        if (_cachedTemplate != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedTemplate;

        var template = TryLoadFromCrd(templateName);
        template ??= TryLoadFromConfigMap();

        _cachedTemplate = template;
        _cacheExpiry = DateTime.UtcNow.Add(CacheTtl);

        return template;
    }

    public void InvalidateCache()
    {
        _cachedTemplate = null;
        _cacheExpiry = DateTime.MinValue;
    }

    private ScriptPodTemplate TryLoadFromCrd(string templateName)
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

    internal ScriptPodTemplate TryLoadFromConfigMap()
    {
        if (_ops == null) return null;
        if (string.IsNullOrWhiteSpace(_settings.ScriptPodTemplateConfigMap)) return null;

        try
        {
            var configMaps = _ops.ListConfigMaps(_settings.TentacleNamespace, $"metadata.name={_settings.ScriptPodTemplateConfigMap}");
            var cm = configMaps?.Items?.FirstOrDefault(c => c.Metadata?.Name == _settings.ScriptPodTemplateConfigMap);

            if (cm?.Data == null || !cm.Data.TryGetValue("template", out var json))
            {
                Log.Debug("No pod template ConfigMap '{ConfigMap}' found in {Namespace}", _settings.ScriptPodTemplateConfigMap, _settings.TentacleNamespace);
                return null;
            }

            var template = JsonSerializer.Deserialize<ScriptPodTemplate>(json, JsonOptions);

            if (template != null)
                Log.Information("Loaded pod template from ConfigMap '{ConfigMap}'", _settings.ScriptPodTemplateConfigMap);

            return template;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load pod template from ConfigMap '{ConfigMap}'", _settings.ScriptPodTemplateConfigMap);
            return null;
        }
    }

    private sealed class CrdWrapper
    {
        public ScriptPodTemplate Spec { get; set; }
    }
}
