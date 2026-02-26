using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

public class GenerateKubernetesAgentInstallScriptCommand : ICommand
{
    public string AgentName { get; set; }
    public string ServerUrl { get; set; }
    public string ServerCommsUrl { get; set; }
    public List<int> EnvironmentIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int SpaceId { get; set; } = 1;
}

public class GenerateKubernetesAgentInstallScriptResponse : SquidResponse<GenerateKubernetesAgentInstallScriptData>
{
}

public class GenerateKubernetesAgentInstallScriptData
{
    public string NfsCsiDriverScript { get; set; }
    public string AgentInstallScript { get; set; }
    public string SubscriptionId { get; set; }
}
