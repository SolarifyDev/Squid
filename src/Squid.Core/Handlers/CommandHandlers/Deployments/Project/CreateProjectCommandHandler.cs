using Squid.Core.Services.Deployments.Project;
using Squid.Message.Commands.Deployments.Project;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Project;

public class CreateProjectCommandHandler : ICommandHandler<CreateProjectCommand, CreateProjectResponse>
{
    private readonly IProjectService _releaseService;

    public CreateProjectCommandHandler(IProjectService releaseService)
    {
        _releaseService = releaseService;
    }
    
    public Task<CreateProjectResponse> Handle(IReceiveContext<CreateProjectCommand> context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}