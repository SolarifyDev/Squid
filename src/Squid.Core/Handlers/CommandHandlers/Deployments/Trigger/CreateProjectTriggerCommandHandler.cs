using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Commands.ProjectTriggers;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Trigger;

public class CreateProjectTriggerCommandHandler : ICommandHandler<CreateProjectTriggerCommand, CreateProjectTriggerResponse>
{
    private readonly IProjectTriggerManagementService _projectTriggerManagementService;

    public CreateProjectTriggerCommandHandler(IProjectTriggerManagementService projectTriggerManagementService)
    {
        _projectTriggerManagementService = projectTriggerManagementService;
    }

    public async Task<CreateProjectTriggerResponse> Handle(IReceiveContext<CreateProjectTriggerCommand> context, CancellationToken cancellationToken)
    {
        var data = await _projectTriggerManagementService.CreateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateProjectTriggerResponse
        {
            Data = data
        };
    }
}
