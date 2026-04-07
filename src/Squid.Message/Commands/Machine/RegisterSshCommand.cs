using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class RegisterSshCommand : ICommand, ISpaceScoped
{
    public string MachineName { get; set; }
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public List<string> Roles { get; set; }
    public List<int> EnvironmentIds { get; set; }

    // Endpoint
    public string Host { get; set; }
    public int Port { get; set; } = 22;
    public string Fingerprint { get; set; }
    public string RemoteWorkingDirectory { get; set; }

    // Resources — must contain one AuthenticationAccount reference (SshKeyPair or UsernamePassword)
    public List<EndpointResourceReference> ResourceReferences { get; set; }
}
