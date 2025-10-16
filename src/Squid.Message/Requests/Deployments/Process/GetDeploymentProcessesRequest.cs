using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process;

public class GetDeploymentProcessesRequest : IPaginatedRequest
{
    public int PageIndex { get; set; } = 1;
    
    public int PageSize { get; set; } = 20;
    
    public Guid? ProjectId { get; set; }
    
    public Guid? SpaceId { get; set; }
    
    public bool? IsFrozen { get; set; }
}

public class GetDeploymentProcessesResponse : SquidResponse<GetDeploymentProcessesResponseData>
{
}

public class GetDeploymentProcessesResponseData
{
    public int Count { get; set; }
    
    public List<DeploymentProcessDto> DeploymentProcesses { get; set; } = new List<DeploymentProcessDto>();
}
