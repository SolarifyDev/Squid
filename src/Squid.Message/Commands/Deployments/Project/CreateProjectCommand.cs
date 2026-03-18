using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

[RequiresPermission(Permission.ProjectCreate)]
public class CreateProjectCommand : ICommand, ISpaceScoped
{
    public CreateOrUpdateProjectModel Project { get; set; }
    int? ISpaceScoped.SpaceId => Project?.SpaceId;
}

public class CreateProjectResponse : SquidResponse<ProjectDto>
{
}

