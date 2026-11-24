using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Requests.ProjectTriggers;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Trigger;

public class GetProjectTriggersRequestHandler : IRequestHandler<GetProjectTriggersRequest, GetProjectTriggersResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public GetProjectTriggersRequestHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<GetProjectTriggersResponse> Handle(IReceiveContext<GetProjectTriggersRequest> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.ListAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetProjectTriggersResponse
        {
            Data = data
        };
    }
}
