using Squid.Core.Services.Deployments.ProjectGroup;
using Squid.Message.Requests.Deployments.ProjectGroup;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.ProjectGroup;

public class GetProjectGroupsRequestHandler : IRequestHandler<GetProjectGroupsRequest, GetProjectGroupsResponse>
{
    private readonly IProjectGroupService _projectGroupService;

    public GetProjectGroupsRequestHandler(IProjectGroupService projectGroupService)
    {
        _projectGroupService = projectGroupService;
    }

    public async Task<GetProjectGroupsResponse> Handle(IReceiveContext<GetProjectGroupsRequest> context, CancellationToken cancellationToken)
    {
        return await _projectGroupService.GetProjectGroupsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
