using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class UpdateMachineCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }

    public string Name { get; set; }

    public bool? IsDisabled { get; set; }

    public List<string> Roles { get; set; }

    public List<int> EnvironmentIds { get; set; }

    public int? MachinePolicyId { get; set; }
}

public class UpdateMachineResponse : SquidResponse<MachineDto>
{
}
