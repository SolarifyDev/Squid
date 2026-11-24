using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Commands.ProjectTriggers;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Trigger;

public class DeleteProjectTriggerCommandHandler : ICommandHandler<DeleteProjectTriggerCommand, DeleteProjectTriggerResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public DeleteProjectTriggerCommandHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<DeleteProjectTriggerResponse> Handle(IReceiveContext<DeleteProjectTriggerCommand> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.DeleteAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new DeleteProjectTriggerResponse
        {
            Data = data
        };
    }
}
