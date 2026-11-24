using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Requests.ProjectTriggers;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Trigger;

public class GetProjectTriggerRequestHandler : IRequestHandler<GetProjectTriggerRequest, GetProjectTriggerResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public GetProjectTriggerRequestHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<GetProjectTriggerResponse> Handle(IReceiveContext<GetProjectTriggerRequest> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.DetailAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetProjectTriggerResponse
        {
            Data = data
        };
    }
}
