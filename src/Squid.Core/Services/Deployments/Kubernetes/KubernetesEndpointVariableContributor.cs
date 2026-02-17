using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Kubernetes;

public class KubernetesEndpointVariableContributor : IEndpointVariableContributor
{
    public bool CanHandle(string communicationStyle)
        => string.Equals(communicationStyle, "Kubernetes", StringComparison.OrdinalIgnoreCase);

    public int? ParseAccountId(string endpointJson)
    {
        var endpoint = Deserialize(endpointJson);
        if (endpoint == null) return null;
        return int.TryParse(endpoint.AccountId, out var id) ? id : null;
    }

    public List<VariableDto> ContributeVariables(string endpointJson, DeploymentAccount account)
    {
        var endpoint = Deserialize(endpointJson);
        if (endpoint == null) return new List<VariableDto>();

        var accountType = account?.AccountType.ToString() ?? "Token";

        return new List<VariableDto>
        {
            MakeVariable("Squid.Action.Kubernetes.ClusterUrl", endpoint.ClusterUrl ?? string.Empty),
            MakeVariable("Squid.Account.AccountType", accountType),
            MakeVariable("Squid.Account.Token", account?.Token ?? string.Empty),
            MakeVariable("Squid.Account.Username", account?.Username ?? string.Empty),
            MakeVariable("Squid.Account.Password", account?.Password ?? string.Empty),
            MakeVariable("Squid.Account.ClientCertificateData", account?.ClientCertificateData ?? string.Empty),
            MakeVariable("Squid.Account.ClientCertificateKeyData", account?.ClientCertificateKeyData ?? string.Empty),
            MakeVariable("Squid.Account.AccessKey", account?.AccessKey ?? string.Empty),
            MakeVariable("Squid.Account.SecretKey", account?.SecretKey ?? string.Empty),
            MakeVariable("Squid.Action.Kubernetes.SkipTlsVerification", endpoint.SkipTlsVerification ?? "False"),
            MakeVariable("Squid.Action.Kubernetes.Namespace", endpoint.Namespace ?? "default"),
            MakeVariable("Squid.Action.Kubernetes.ClusterCertificate", endpoint.ClusterCertificate ?? string.Empty),
            MakeVariable("Squid.Action.Script.SuppressEnvironmentLogging", "False"),
            MakeVariable("Squid.Action.Kubernetes.OutputKubectlVersion", "True"),
            MakeVariable("SquidPrintEvaluatedVariables", "True")
        };
    }

    private static KubernetesEndpointDto Deserialize(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<KubernetesEndpointDto>(endpointJson);
        }
        catch
        {
            return null;
        }
    }

    private static VariableDto MakeVariable(string name, string value) => new()
    {
        Name = name,
        Value = value,
        Description = string.Empty,
        Type = Message.Enums.VariableType.String,
        IsSensitive = false,
        LastModifiedOn = DateTimeOffset.UtcNow,
        LastModifiedBy = "System"
    };
}
