using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class GenerateKubernetesAgentUpgradeScriptCommand : ICommand
{
    public int MachineId { get; set; }
}

public class GenerateKubernetesAgentUpgradeScriptResponse : SquidResponse<GenerateKubernetesAgentUpgradeScriptData>
{
}

public class GenerateKubernetesAgentUpgradeScriptData
{
    public int MachineId { get; set; }
    public string CurrentVersion { get; set; }
    public string LatestVersion { get; set; }
    public bool NeedsUpgrade { get; set; }
    public string ReleaseName { get; set; }
    public string HelmNamespace { get; set; }
    public string ChartRef { get; set; }
    public string UpgradeScript { get; set; }
}
