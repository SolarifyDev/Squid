using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Runbook;

[RequiresPermission(Permission.RunbookRun)]
public class RunRunbookCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int? SpaceId { get; set; }

    public int EnvironmentId { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class RunRunbookResponse : SquidResponse<RunbookRunDto>
{
}
