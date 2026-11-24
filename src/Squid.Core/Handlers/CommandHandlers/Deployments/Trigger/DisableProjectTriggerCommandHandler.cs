using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Commands.ProjectTriggers;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Trigger;

public class DisableProjectTriggerCommandHandler : ICommandHandler<DisableProjectTriggerCommand, DisableProjectTriggerResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public DisableProjectTriggerCommandHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<DisableProjectTriggerResponse> Handle(IReceiveContext<DisableProjectTriggerCommand> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.DisableAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new DisableProjectTriggerResponse
        {
            Data = data
        };
    }
}
