using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class GenerateTentacleInstallScriptCommand : ICommand, ISpaceScoped
{
    // Target machine metadata
    public string MachineName { get; set; }
    public List<string> Environments { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int SpaceId { get; set; } = 1;
    int? ISpaceScoped.SpaceId => SpaceId;

    // Server connection
    public string ServerUrl { get; set; }
    public string ServerCommsUrl { get; set; }

    // Communication mode
    public string CommunicationMode { get; set; } = "Listening";
    public string ListeningHostName { get; set; }
    public int ListeningPort { get; set; } = 10933;

    // Filter: only generate scripts for this OS. Null = return all.
    public string OperatingSystem { get; set; }

    // Method-specific overrides
    public string DockerImage { get; set; }
}

public class GenerateTentacleInstallScriptResponse : SquidResponse<GenerateTentacleInstallScriptData>
{
}

public class GenerateTentacleInstallScriptData
{
    public string ServerThumbprint { get; set; }
    public List<TentacleInstallScript> Scripts { get; set; } = [];
}

public class TentacleInstallScript
{
    public string Id { get; set; }
    public string Label { get; set; }
    public string OperatingSystem { get; set; }
    public string InstallationMethod { get; set; }
    public string ScriptType { get; set; }
    public string Content { get; set; }
    public bool IsRecommended { get; set; }
}
