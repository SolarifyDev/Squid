using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Machines;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineEdit)]
public class RunMachineHealthCheckCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int MachineId { get; set; }
}

/// <summary>
/// H3 — Carries the structured <see cref="ManualHealthCheckResult"/> payload
/// so the FE / CLI can distinguish probe outcomes (agent_unreachable vs
/// machine_disabled vs success-with-fresh-OS) without re-parsing the
/// human-readable detail line.
/// </summary>
public class RunMachineHealthCheckResponse : SquidResponse<ManualHealthCheckResult>
{
}
