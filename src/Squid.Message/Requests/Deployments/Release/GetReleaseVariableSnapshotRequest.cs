using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

[RequiresPermission(Permission.ReleaseView)]
public class GetReleaseVariableSnapshotRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ReleaseId { get; set; }
}

public class GetReleaseVariableSnapshotResponse : SquidResponse<GetReleaseVariableSnapshotResponseData>
{
}

public class GetReleaseVariableSnapshotResponseData
{
    public VariableSetSnapshotDto VariableSnapshot { get; set; }
}
