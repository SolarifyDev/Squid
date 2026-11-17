using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Project;

[RequiresPermission(Permission.ProjectView)]
public class GetProjectProgressionRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ProjectId { get; set; }

    /// <summary>
    /// How many of the most recent releases per channel the dashboard returns, on top of every release that
    /// holds a current or previous-successful deployment. Null uses <see cref="DefaultReleaseHistoryCount"/>
    /// (Octopus' dashboard window); values above <see cref="MaxReleaseHistoryCount"/> are clamped.
    /// </summary>
    public int? ReleaseHistoryCount { get; set; }

    public const int DefaultReleaseHistoryCount = 3;
    public const int MaxReleaseHistoryCount = 50;
}

public class GetProjectProgressionResponse : SquidResponse<ProjectProgressionDto>
{
}
