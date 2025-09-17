using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process;

public class UpdateDeploymentProcessCommand : ICommand
{
    public Guid Id { get; set; }
    
    public string Name { get; set; }
    
    public string Description { get; set; }
    
    public bool IsFrozen { get; set; }
    
    public string LastModifiedBy { get; set; }
    
    public List<DeploymentStepDto> Steps { get; set; } = new List<DeploymentStepDto>();
}

public class UpdateDeploymentProcessResponse : SquidResponse<UpdateDeploymentProcessResponseData>
{
}

public class UpdateDeploymentProcessResponseData
{
    public DeploymentProcessDto DeploymentProcess { get; set; }
}
