using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ProjectGroup;

public class UpdateProjectGroupCommand : ICommand
{
    public ProjectGroupDto ProjectGroup { get; set; }
}

public class UpdateProjectGroupResponse : SquidResponse<ProjectGroupDto>
{
}
