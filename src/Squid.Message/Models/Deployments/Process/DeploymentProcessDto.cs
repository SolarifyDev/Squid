namespace Squid.Message.Models.Deployments.Process;

public class DeploymentProcessDto
{
    public int Id { get; set; }
    
    public int ProjectId { get; set; }
    
    public int Version { get; set; }
    
    public string Name { get; set; }
    
    public string Description { get; set; }
    
    public bool IsFrozen { get; set; }
    
    public int SpaceId { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public string CreatedBy { get; set; }
    
    public DateTimeOffset LastModified { get; set; }
    
    public string LastModifiedBy { get; set; }
    
    public List<DeploymentStepDto> Steps { get; set; } = new List<DeploymentStepDto>();
}
