using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

public class GetReleaseVariableSnapshotRequest : IRequest
{
    public int ReleaseId { get; set; }
}

public class GetReleaseVariableSnapshotResponse : SquidResponse<GetReleaseVariableSnapshotResponseData>
{
}

public class GetReleaseVariableSnapshotResponseData
{
    public VariableSetSnapshotDto VariableSnapshot { get; set; }
}
