using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Requests.ProjectTriggers;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Trigger;

public class GetProjectTriggerTypesRequestHandler : IRequestHandler<GetProjectTriggerTypesRequest, GetProjectTriggerTypesResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public GetProjectTriggerTypesRequestHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<GetProjectTriggerTypesResponse> Handle(IReceiveContext<GetProjectTriggerTypesRequest> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.TypesAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetProjectTriggerTypesResponse
        {
            Data = data
        };
    }
}
