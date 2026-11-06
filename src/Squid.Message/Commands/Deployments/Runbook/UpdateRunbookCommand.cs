using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Runbook;

public class UpdateRunbookCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    public UpdateRunbookModel Runbook { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class UpdateRunbookResponse : SquidResponse<RunbookDto>
{
}
