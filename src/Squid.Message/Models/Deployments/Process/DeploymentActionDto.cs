namespace Squid.Message.Models.Deployments.Process;

public class DeploymentActionDto
{
    public Guid Id { get; set; }
    
    public Guid StepId { get; set; }
    
    public int ActionOrder { get; set; }
    
    public string Name { get; set; }
    
    public string ActionType { get; set; }
    
    public Guid? WorkerPoolId { get; set; }
    
    public bool IsDisabled { get; set; }
    
    public bool IsRequired { get; set; }
    
    public bool CanBeUsedForProjectVersioning { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public List<DeploymentActionPropertyDto> Properties { get; set; } = new List<DeploymentActionPropertyDto>();
    
    public List<Guid> Environments { get; set; } = new List<Guid>();
    
    public List<Guid> Channels { get; set; } = new List<Guid>();
    
    public List<string> TenantTags { get; set; } = new List<string>();
    
    public List<string> MachineRoles { get; set; } = new List<string>();
}
