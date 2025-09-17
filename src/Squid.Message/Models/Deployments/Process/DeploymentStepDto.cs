namespace Squid.Message.Models.Deployments.Process;

public class DeploymentStepDto
{
    public Guid Id { get; set; }
    
    public Guid ProcessId { get; set; }
    
    public int StepOrder { get; set; }
    
    public string Name { get; set; }
    
    public string StepType { get; set; }
    
    public string Condition { get; set; }
    
    public string StartTrigger { get; set; }
    
    public string PackageRequirement { get; set; }
    
    public bool IsDisabled { get; set; }
    
    public bool IsRequired { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    
    public List<DeploymentStepPropertyDto> Properties { get; set; } = new List<DeploymentStepPropertyDto>();
    
    public List<DeploymentActionDto> Actions { get; set; } = new List<DeploymentActionDto>();
}
