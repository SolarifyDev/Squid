using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

public class UpdateProjectCommand : ICommand
{
    public ProjectDto Project { get; set; }
}

public class UpdateProjectResponse : SquidResponse<ProjectDto>
{
}

