namespace Squid.Message.Models.Deployments.Process;

public class DeploymentActionDto
{
    public int Id { get; set; }
    
    public int StepId { get; set; }
    
    public int ActionOrder { get; set; }
    
    public string Name { get; set; }
    
    public string ActionType { get; set; }
    
    public int? WorkerPoolId { get; set; }
    
    public bool IsDisabled { get; set; }
    
    public bool IsRequired { get; set; }
    
    public bool CanBeUsedForProjectVersioning { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public List<DeploymentActionPropertyDto> Properties { get; set; } = new List<DeploymentActionPropertyDto>();
    
    public List<int> Environments { get; set; } = new List<int>();
    
    public List<int> Channels { get; set; } = new List<int>();
    
    public List<string> TenantTags { get; set; } = new List<string>();
    
    public List<string> MachineRoles { get; set; } = new List<string>();
}
