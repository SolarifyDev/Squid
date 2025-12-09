using Squid.Core.Services.Deployments.Project;
using Squid.Message.Commands.Deployments.Project;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Project;

public class UpdateProjectCommandHandler : ICommandHandler<UpdateProjectCommand, UpdateProjectResponse>
{
    private readonly IProjectService _projectService;

    public UpdateProjectCommandHandler(IProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<UpdateProjectResponse> Handle(IReceiveContext<UpdateProjectCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _projectService.UpdateProjectAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateProjectResponse
        {
            Data = @event.Data
        };
    }
}

