using Squid.Core.Services.Deployments.Project;
using Squid.Message.Requests.Deployments.Project;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Project;

public class GetProjectSummariesRequestHandler : IRequestHandler<GetProjectSummariesRequest, GetProjectSummariesResponse>
{
    private readonly IProjectService _projectService;

    public GetProjectSummariesRequestHandler(IProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<GetProjectSummariesResponse> Handle(IReceiveContext<GetProjectSummariesRequest> context, CancellationToken cancellationToken)
    {
        return await _projectService.GetProjectSummariesAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
