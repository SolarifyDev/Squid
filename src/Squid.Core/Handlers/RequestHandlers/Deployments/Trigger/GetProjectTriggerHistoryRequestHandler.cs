using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Requests.ProjectTriggers;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Trigger;

public class GetProjectTriggerHistoryRequestHandler : IRequestHandler<GetProjectTriggerHistoryRequest, GetProjectTriggerHistoryResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public GetProjectTriggerHistoryRequestHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<GetProjectTriggerHistoryResponse> Handle(IReceiveContext<GetProjectTriggerHistoryRequest> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.HistoryAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetProjectTriggerHistoryResponse
        {
            Data = data
        };
    }
}
