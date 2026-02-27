using Squid.Core.Services.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.ProjectGroup;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ProjectGroup;

public class DeleteProjectGroupsCommandHandler : ICommandHandler<DeleteProjectGroupsCommand, DeleteProjectGroupsResponse>
{
    private readonly IProjectGroupService _projectGroupService;

    public DeleteProjectGroupsCommandHandler(IProjectGroupService projectGroupService)
    {
        _projectGroupService = projectGroupService;
    }

    public async Task<DeleteProjectGroupsResponse> Handle(IReceiveContext<DeleteProjectGroupsCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _projectGroupService.DeleteProjectGroupsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteProjectGroupsResponse
        {
            Data = @event.Data
        };
    }
}
