using Squid.Core.Services.Deployments.Project;
using Squid.Message.Commands.Deployments.Project;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Project;

public class DeleteProjectsCommandHandler : ICommandHandler<DeleteProjectsCommand, DeleteProjectsResponse>
{
    private readonly IProjectService _projectService;

    public DeleteProjectsCommandHandler(IProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<DeleteProjectsResponse> Handle(IReceiveContext<DeleteProjectsCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _projectService.DeleteProjectsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteProjectsResponse
        {
            Data = @event.Data
        };
    }
}

