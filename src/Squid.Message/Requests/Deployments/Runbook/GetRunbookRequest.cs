using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Runbook;

public class GetRunbookRequest : IRequest, ISpaceScoped
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class GetRunbookResponse : SquidResponse<RunbookDto>
{
}
