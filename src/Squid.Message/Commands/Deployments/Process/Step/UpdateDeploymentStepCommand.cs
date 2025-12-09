using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process.Step;

public class UpdateDeploymentStepCommand : ICommand
{
    public int Id { get; set; }

    public DeploymentStepDto Step { get; set; }
}

public class UpdateDeploymentStepResponse : SquidResponse<DeploymentStepDto>
{
}

