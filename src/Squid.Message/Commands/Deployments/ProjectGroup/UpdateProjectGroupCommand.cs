using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ProjectGroup;

[RequiresPermission(Permission.ProjectEdit)]
public class UpdateProjectGroupCommand : ICommand
{
    public CreateOrUpdateProjectGroupModel ProjectGroup { get; set; }
}

public class UpdateProjectGroupResponse : SquidResponse<ProjectGroupDto>
{
}
