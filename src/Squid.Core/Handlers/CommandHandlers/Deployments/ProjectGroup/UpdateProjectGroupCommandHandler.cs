using Squid.Core.Services.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.ProjectGroup;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ProjectGroup;

public class UpdateProjectGroupCommandHandler : ICommandHandler<UpdateProjectGroupCommand, UpdateProjectGroupResponse>
{
    private readonly IProjectGroupService _projectGroupService;

    public UpdateProjectGroupCommandHandler(IProjectGroupService projectGroupService)
    {
        _projectGroupService = projectGroupService;
    }

    public async Task<UpdateProjectGroupResponse> Handle(IReceiveContext<UpdateProjectGroupCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _projectGroupService.UpdateProjectGroupAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateProjectGroupResponse
        {
            Data = @event.Data
        };
    }
}
