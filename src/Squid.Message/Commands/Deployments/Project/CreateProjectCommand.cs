using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

public class CreateProjectCommand : ICommand
{
    public string Name { get; set; }
    
    public int ProjectGroupId { get; set; }
    
    public int LifecycleId { get; set; }
    
    public string Description { get; set; }
    
    public int DeploymentProcessId { get; set; }
}

public class CreateProjectResponse : SquidResponse<ProjectDto>
{
}