using System.Text.Json;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public static class KubernetesApiEndpointProviderConfigConverter
{
    public static object Deserialize(KubernetesApiEndpointProviderType providerType, string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        return providerType switch
        {
            KubernetesApiEndpointProviderType.AwsEks => JsonSerializer.Deserialize<KubernetesApiAwsEksConfig>(json, SquidJsonDefaults.CaseInsensitive),
            KubernetesApiEndpointProviderType.AzureAks => JsonSerializer.Deserialize<KubernetesApiAzureAksConfig>(json, SquidJsonDefaults.CaseInsensitive),
            KubernetesApiEndpointProviderType.GcpGke => JsonSerializer.Deserialize<KubernetesApiGcpGkeConfig>(json, SquidJsonDefaults.CaseInsensitive),
            _ => null
        };
    }

    public static string Serialize(object config)
    {
        if (config == null) return null;

        return JsonSerializer.Serialize(config, config.GetType());
    }
}
