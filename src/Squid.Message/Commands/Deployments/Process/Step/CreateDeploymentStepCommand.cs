using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process.Step;

public class CreateDeploymentStepCommand : ICommand
{
    public int ProcessId { get; set; }

    public DeploymentStepDto Step { get; set; }
}

public class CreateDeploymentStepResponse : SquidResponse<DeploymentStepDto>
{
}

