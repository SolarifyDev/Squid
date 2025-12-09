using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

public class CreateProjectGroupCommand : ICommand
{
    public string Name { get; set; }
    
    public string Description { get; set; }
}

public class CreateProjectGroupResponse : SquidResponse<ProjectGroupDto>
{
    
}
