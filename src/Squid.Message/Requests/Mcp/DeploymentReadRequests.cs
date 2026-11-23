using Squid.Message.Response;

namespace Squid.Message.Requests.Mcp;

public sealed class ListVisibleDeploymentsRequest : IRequest, IScopedReadQuery
{
    public int SpaceId { get; set; }
    public int ProjectId { get; set; }
    public int EnvironmentId { get; set; }
    public int PageSize { get; set; } = 20;
    public ScopedReadPosition? After { get; set; }
}

public sealed class GetVisibleDeploymentTaskRequest : IRequest, IScopedReadQuery
{
    public int SpaceId { get; set; }
    public int TaskId { get; set; }
}

public sealed record DeploymentReadItem(
    int Id,
    int ReleaseId,
    string? CurrentReleaseVersion,
    int? TaskId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record DeploymentTaskReadItem(
    int TaskId,
    int DeploymentId,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed class ListVisibleDeploymentsResponse
    : SquidResponse<ScopedReadPage<DeploymentReadItem>>
{
    public DeploymentReadItem? LatestSuccessfulDeployment { get; set; }
}

public sealed class GetVisibleDeploymentTaskResponse : SquidResponse<DeploymentTaskReadItem>;
