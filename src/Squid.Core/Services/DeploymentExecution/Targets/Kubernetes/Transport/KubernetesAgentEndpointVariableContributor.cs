using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;

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
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.Namespace, endpoint.Namespace ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.SuppressEnvironmentLogging, KubernetesBooleanValues.False),
            EndpointVariableFactory.Make(SpecialVariables.Kubernetes.PrintEvaluatedVariables, KubernetesBooleanValues.True)
        };
    }
}
