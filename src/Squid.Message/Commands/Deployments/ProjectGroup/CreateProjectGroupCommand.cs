using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ProjectGroup;

public class CreateProjectGroupCommand : ICommand
{
    public CreateOrUpdateProjectGroupModel ProjectGroup { get; set; }
}

public class CreateProjectGroupResponse : SquidResponse<ProjectGroupDto>
{
}
