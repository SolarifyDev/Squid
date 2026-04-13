using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class RegisterLinuxListeningCommand : ICommand, ISpaceScoped
{
    public string MachineName { get; set; }
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public List<string> Roles { get; set; }
    public List<int> EnvironmentIds { get; set; }
    public string Uri { get; set; }
    public string Thumbprint { get; set; }
    public string AgentVersion { get; set; }
}
