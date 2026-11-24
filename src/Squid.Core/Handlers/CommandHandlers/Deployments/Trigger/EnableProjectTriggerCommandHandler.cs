using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Commands.ProjectTriggers;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Trigger;

public class EnableProjectTriggerCommandHandler : ICommandHandler<EnableProjectTriggerCommand, EnableProjectTriggerResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public EnableProjectTriggerCommandHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<EnableProjectTriggerResponse> Handle(IReceiveContext<EnableProjectTriggerCommand> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.EnableAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new EnableProjectTriggerResponse
        {
            Data = data
        };
    }
}
