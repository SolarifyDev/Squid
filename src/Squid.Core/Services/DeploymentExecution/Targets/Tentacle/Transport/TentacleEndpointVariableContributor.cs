using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Machines;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

public class TentacleEndpointVariableContributor : IEndpointVariableContributor
{
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;

    public TentacleEndpointVariableContributor() : this(capabilitiesCache: null) { }

    public TentacleEndpointVariableContributor(IMachineRuntimeCapabilitiesCache capabilitiesCache)
    {
        _capabilitiesCache = capabilitiesCache;
    }

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

        if (style == nameof(Message.Enums.CommunicationStyle.TentacleListening))
        {
            var uri = EndpointJsonHelper.GetField(context.EndpointJson, "Uri");
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.Uri", uri ?? string.Empty));
        }
        else
        {
            var subscriptionId = EndpointJsonHelper.GetField(context.EndpointJson, "SubscriptionId");
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.SubscriptionId", subscriptionId ?? string.Empty));
        }

        ContributeRuntimeCapabilities(context, vars);

        return vars;
    }

    private void ContributeRuntimeCapabilities(EndpointContext context, List<VariableDto> vars)
    {
        if (_capabilitiesCache == null || context.MachineId == null) return;

        var caps = _capabilitiesCache.TryGet(context.MachineId.Value);

        if (!string.IsNullOrEmpty(caps.Os))
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.OS", caps.Os));

        if (!string.IsNullOrEmpty(caps.DefaultShell))
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.DefaultShell", caps.DefaultShell));

        if (!string.IsNullOrEmpty(caps.InstalledShells))
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.InstalledShells", caps.InstalledShells));

        if (!string.IsNullOrEmpty(caps.Architecture))
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.Architecture", caps.Architecture));

        if (!string.IsNullOrEmpty(caps.AgentVersion))
            vars.Add(EndpointVariableFactory.Make("Squid.Tentacle.AgentVersion", caps.AgentVersion));
    }
}
