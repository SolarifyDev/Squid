using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process.Step;

public class CreateDeploymentStepCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ProcessId { get; set; }
    public int? ProjectId { get; set; }
    public int? RunbookId { get; set; }

    public CreateOrUpdateDeploymentStepModel Step { get; set; }
}

public class CreateDeploymentStepResponse : SquidResponse<DeploymentStepDto>
{
}
