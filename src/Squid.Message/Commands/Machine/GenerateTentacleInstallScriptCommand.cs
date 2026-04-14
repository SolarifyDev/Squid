using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class GenerateTentacleInstallScriptCommand : ICommand, ISpaceScoped
{
    public string MachineName { get; set; }
    public string ServerUrl { get; set; }
    public string ServerCommsUrl { get; set; }
    public List<string> Environments { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int SpaceId { get; set; } = 1;
    int? ISpaceScoped.SpaceId => SpaceId;
    public string CommunicationMode { get; set; } = "Listening";
    public string ListeningHostName { get; set; }
    public int ListeningPort { get; set; } = 10933;
    public string DockerImage { get; set; }
}

public class GenerateTentacleInstallScriptResponse : SquidResponse<GenerateTentacleInstallScriptData>
{
}

public class GenerateTentacleInstallScriptData
{
    public string DockerRunScript { get; set; }
    public string ScriptInstallScript { get; set; }
    public string DockerComposeScript { get; set; }
    public string ServerThumbprint { get; set; }
}
