using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process.Step;

[RequiresPermission(Permission.ProcessEdit)]
public class UpdateDeploymentStepCommand : ICommand
{
    public int Id { get; set; }

    public CreateOrUpdateDeploymentStepModel Step { get; set; }
}

public class UpdateDeploymentStepResponse : SquidResponse<DeploymentStepDto>
{
}

