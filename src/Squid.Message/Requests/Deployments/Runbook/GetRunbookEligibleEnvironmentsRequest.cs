using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Runbook;

[RequiresPermission(Permission.RunbookRun)]
public class GetRunbookEligibleEnvironmentsRequest : IRequest, ISpaceScoped
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int? SpaceId { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class GetRunbookEligibleEnvironmentsResponse
    : SquidResponse<GetRunbookEligibleEnvironmentsResponseData>
{
}

public class GetRunbookEligibleEnvironmentsResponseData
{
    public List<EnvironmentDto> Environments { get; set; } = new();
}
