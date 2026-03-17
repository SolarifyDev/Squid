using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

[RequiresPermission(Permission.ProjectCreate)]
public class CreateProjectCommand : ICommand
{
    public CreateOrUpdateProjectModel Project { get; set; }
}

public class CreateProjectResponse : SquidResponse<ProjectDto>
{
}

