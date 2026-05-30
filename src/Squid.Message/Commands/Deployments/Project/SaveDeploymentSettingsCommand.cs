using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Project;

[RequiresPermission(Permission.ProjectEdit)]
public class SaveDeploymentSettingsCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ProjectId { get; set; }
    public DeploymentSettingsDto DeploymentSettings { get; set; } = new();
}

public class SaveDeploymentSettingsResponse : SquidResponse<DeploymentSettingsDto>
{
}
