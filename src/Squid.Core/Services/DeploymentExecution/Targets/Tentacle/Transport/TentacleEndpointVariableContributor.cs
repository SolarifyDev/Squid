using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Machines;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

public class TentacleEndpointVariableContributor : IEndpointVariableContributor
{
    public EndpointResourceReferences ParseResourceReferences(string endpointJson) => new();

    public List<VariableDto> ContributeVariables(EndpointContext context)
    {
        var style = EndpointJsonHelper.GetField(context.EndpointJson, "CommunicationStyle");

        if (string.IsNullOrEmpty(style))
            return new List<VariableDto>();

        var thumbprint = EndpointJsonHelper.GetField(context.EndpointJson, "Thumbprint");

        var vars = new List<VariableDto>
        {
            EndpointVariableFactory.Make("Squid.Tentacle.CommunicationStyle", style),
            EndpointVariableFactory.Make("Squid.Tentacle.Thumbprint", thumbprint ?? string.Empty)
        };

        if (style == nameof(Message.Enums.CommunicationStyle.LinuxListening))
        {
            var uri = EndpointJsonHelper.GetField(context.EndpointJson, "Uri");
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.Uri", uri ?? string.Empty));
        }
        else
        {
            var subscriptionId = EndpointJsonHelper.GetField(context.EndpointJson, "SubscriptionId");
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.SubscriptionId", subscriptionId ?? string.Empty));
        }

        return vars;
    }
}
