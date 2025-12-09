using Squid.Core.Services.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Project;

public class GetProjectsRequestHandler : IRequestHandler<GetProjectsRequest, GetProjectsResponse>
{
    private readonly IProjectService _projectService;

    public GetProjectsRequestHandler(IProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<GetProjectsResponse> Handle(IReceiveContext<GetProjectsRequest> context, CancellationToken cancellationToken)
    {
        return await _projectService.GetProjectsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}

