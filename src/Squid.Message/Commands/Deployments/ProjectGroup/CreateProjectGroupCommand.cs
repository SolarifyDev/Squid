using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ProjectGroup;

[RequiresPermission(Permission.ProjectCreate)]
public class CreateProjectGroupCommand : ICommand, ISpaceScoped
{
    public CreateOrUpdateProjectGroupModel ProjectGroup { get; set; }
    int? ISpaceScoped.SpaceId => ProjectGroup?.SpaceId;
}

public class CreateProjectGroupResponse : SquidResponse<ProjectGroupDto>
{
}
