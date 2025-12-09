using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process;

public class CreateDeploymentProcessCommand : ICommand
{
    public int ProjectId { get; set; }
    
    public string Name { get; set; }
    
    public string Description { get; set; }
    
    public int SpaceId { get; set; }
    
    public string CreatedBy { get; set; }
}

public class CreateDeploymentProcessResponse : SquidResponse<CreateDeploymentProcessResponseData>
{
}

public class CreateDeploymentProcessResponseData
{
    public DeploymentProcessDto DeploymentProcess { get; set; }
}
