using Squid.Core.Services.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Project;

public class GetProjectProgressionRequestHandler : IRequestHandler<GetProjectProgressionRequest, GetProjectProgressionResponse>
{
    private readonly IProjectService _projectService;

    public GetProjectProgressionRequestHandler(IProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<GetProjectProgressionResponse> Handle(IReceiveContext<GetProjectProgressionRequest> context, CancellationToken cancellationToken)
    {
        return await _projectService.GetProjectProgressionAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
