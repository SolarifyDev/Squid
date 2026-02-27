using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesAgentEndpointVariableContributor : IEndpointVariableContributor
{
    public EndpointResourceReferences ParseResourceReferences(string endpointJson) => new();

    public List<VariableDto> ContributeVariables(EndpointContext context)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesAgentEndpointDto>(context.EndpointJson);

        if (endpoint == null) return new List<VariableDto>();

        return new List<VariableDto>
        {
            EndpointVariableFactory.Make("Squid.Action.Kubernetes.Namespace", endpoint.Namespace ?? "default"),
            EndpointVariableFactory.Make("Squid.Action.Script.SuppressEnvironmentLogging", "False"),
            EndpointVariableFactory.Make("SquidPrintEvaluatedVariables", "True")
        };
    }
}
