using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Runbook;

[RequiresPermission(Permission.RunbookView)]
public class GetRunbooksRequest : IPaginatedRequest, ISpaceScoped
{
    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    public int PageIndex { get; set; }

    public int PageSize { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class GetRunbooksResponse : SquidResponse<GetRunbooksResponseData>
{
}

public class GetRunbooksResponseData
{
    public int Count { get; set; }

    public List<RunbookDto> Runbooks { get; set; } = new();
}
