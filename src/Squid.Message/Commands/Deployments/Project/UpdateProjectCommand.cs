using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

[RequiresPermission(Permission.ProjectEdit)]
public class UpdateProjectCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }
    public CreateOrUpdateProjectModel Project { get; set; }
    int? ISpaceScoped.SpaceId => Project?.SpaceId;
}

public class UpdateProjectResponse : SquidResponse<ProjectDto>
{
}

