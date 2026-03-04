namespace Squid.Message.Models.Deployments.Process;

public class CreateOrUpdateDeploymentStepModel
{
    public string Name { get; set; }
    public string StepType { get; set; }
    public string Condition { get; set; }
    public string StartTrigger { get; set; }
    public string PackageRequirement { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsRequired { get; set; }
    public List<StepPropertyModel> Properties { get; set; } = new();
    public List<CreateOrUpdateDeploymentActionModel> Actions { get; set; } = new();
}
