using Squid.Core.Services.Deployments.Project;
using Squid.Message.Commands.Deployments.Project;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Project;

public class CreateProjectCommandHandler : ICommandHandler<CreateProjectCommand, CreateProjectResponse>
{
    private readonly IProjectService _projectService;

    public CreateProjectCommandHandler(IProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<CreateProjectResponse> Handle(IReceiveContext<CreateProjectCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _projectService.CreateProjectAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateProjectResponse
        {
            Data = @event.Data
        };
    }
}

