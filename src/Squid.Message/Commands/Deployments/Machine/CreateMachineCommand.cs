using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Machine;

public class CreateMachineCommand : ICommand
{
    public string Name { get; set; }

    public bool IsDisabled { get; set; }

    public string Roles { get; set; }

    public string EnvironmentIds { get; set; }

    public string Json { get; set; }

    public int? MachinePolicyId { get; set; }

    public string Thumbprint { get; set; }

    public string Uri { get; set; }

    public bool HasLatestCalamari { get; set; }

    public string Endpoint { get; set; }

    public int SpaceId { get; set; }

    public string OperatingSystem { get; set; }

    public string ShellName { get; set; }

    public string ShellVersion { get; set; }

    public string LicenseHash { get; set; }

    public string Slug { get; set; }
}

public class CreateMachineResponse : SquidResponse<CreateMachineResponseData>
{
}

public class CreateMachineResponseData
{
    public MachineDto Machine { get; set; }
} 