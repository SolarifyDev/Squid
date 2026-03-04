using Squid.Core.Services.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.ProjectGroup;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ProjectGroup;

public class CreateProjectGroupCommandHandler : ICommandHandler<CreateProjectGroupCommand, CreateProjectGroupResponse>
{
    private readonly IProjectGroupService _projectGroupService;

    public CreateProjectGroupCommandHandler(IProjectGroupService projectGroupService)
    {
        _projectGroupService = projectGroupService;
    }

    public async Task<CreateProjectGroupResponse> Handle(IReceiveContext<CreateProjectGroupCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _projectGroupService.CreateProjectGroupAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateProjectGroupResponse
        {
            Data = @event.Data
        };
    }
}
