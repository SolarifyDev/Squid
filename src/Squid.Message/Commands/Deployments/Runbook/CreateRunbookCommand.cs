using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Runbook;

public class CreateRunbookCommand : ICommand, ISpaceScoped
{
    public CreateRunbookModel Runbook { get; set; }

    int? ISpaceScoped.SpaceId => Runbook?.SpaceId;
}

public class CreateRunbookResponse : SquidResponse<RunbookDto>
{
}
