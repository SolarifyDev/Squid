using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentEndpointVariableContributor : IEndpointVariableContributor
{
    public bool CanHandle(string communicationStyle)
        => string.Equals(communicationStyle, "KubernetesAgent", StringComparison.OrdinalIgnoreCase);

    public int? ParseAccountId(string endpointJson) => null;

    public List<VariableDto> ContributeVariables(string endpointJson, DeploymentAccount account)
    {
        var endpoint = Deserialize(endpointJson);
        if (endpoint == null) return new List<VariableDto>();

        return new List<VariableDto>
        {
            MakeVariable("Squid.Action.Kubernetes.Namespace", endpoint.Namespace ?? "default"),
            MakeVariable("Squid.Action.Script.SuppressEnvironmentLogging", "False"),
            MakeVariable("SquidPrintEvaluatedVariables", "True")
        };
    }

    private static KubernetesAgentEndpointVariableDto Deserialize(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<KubernetesAgentEndpointVariableDto>(endpointJson);
        }
        catch
        {
            return null;
        }
    }

    private static VariableDto MakeVariable(string name, string value, bool isSensitive = false) => new()
    {
        Name = name,
        Value = value,
        Description = string.Empty,
        Type = Message.Enums.VariableType.String,
        IsSensitive = isSensitive,
        LastModifiedOn = DateTimeOffset.UtcNow,
        LastModifiedBy = "System"
    };

    private class KubernetesAgentEndpointVariableDto
    {
        public string Namespace { get; set; }
    }
}
