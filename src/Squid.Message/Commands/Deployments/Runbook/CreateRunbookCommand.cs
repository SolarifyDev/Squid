using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Runbook;

[RequiresPermission(Permission.RunbookEdit)]
public class CreateRunbookCommand : ICommand, ISpaceScoped
{
    public CreateRunbookModel Runbook { get; set; }

    int? ISpaceScoped.SpaceId => Runbook?.SpaceId;
}

public class CreateRunbookResponse : SquidResponse<RunbookDto>
{
}
