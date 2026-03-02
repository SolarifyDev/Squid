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
            EndpointVariableFactory.Make(KubernetesProperties.LegacyNamespace, endpoint.Namespace ?? KubernetesDefaultValues.Namespace),
            EndpointVariableFactory.Make(KubernetesScriptProperties.SuppressEnvironmentLogging, KubernetesBooleanValues.False),
            EndpointVariableFactory.Make(KubernetesCommonVariableNames.PrintEvaluatedVariables, KubernetesBooleanValues.True)
        };
    }
}
