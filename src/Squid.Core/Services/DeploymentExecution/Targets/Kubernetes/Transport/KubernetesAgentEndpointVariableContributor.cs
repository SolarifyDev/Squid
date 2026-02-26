using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentEndpointVariableContributor : IEndpointVariableContributor
{
    public int? ParseAccountId(string endpointJson) => null;

    public List<VariableDto> ContributeVariables(string endpointJson, DeploymentAccount account)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesAgentEndpointDto>(endpointJson);
        
        if (endpoint == null) return new List<VariableDto>();

        return new List<VariableDto>
        {
            EndpointVariableFactory.Make("Squid.Action.Kubernetes.Namespace", endpoint.Namespace ?? "default"),
            EndpointVariableFactory.Make("Squid.Action.Script.SuppressEnvironmentLogging", "False"),
            EndpointVariableFactory.Make("SquidPrintEvaluatedVariables", "True")
        };
    }
}
