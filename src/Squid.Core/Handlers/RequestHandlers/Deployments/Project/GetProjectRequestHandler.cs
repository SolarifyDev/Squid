using Squid.Core.Services.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Project;

public class GetProjectRequestHandler : IRequestHandler<GetProjectRequest, GetProjectResponse>
{
    private readonly IProjectService _projectService;

    public GetProjectRequestHandler(IProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<GetProjectResponse> Handle(IReceiveContext<GetProjectRequest> context, CancellationToken cancellationToken)
    {
        return await _projectService.GetProjectByIdAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);
    }
}

