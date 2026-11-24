using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Commands.ProjectTriggers;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Trigger;

public class UpdateProjectTriggerCommandHandler : ICommandHandler<UpdateProjectTriggerCommand, UpdateProjectTriggerResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public UpdateProjectTriggerCommandHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<UpdateProjectTriggerResponse> Handle(IReceiveContext<UpdateProjectTriggerCommand> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.UpdateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdateProjectTriggerResponse
        {
            Data = data
        };
    }
}
