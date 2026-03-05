namespace Squid.Message.Models.Deployments.Process;

public class CreateOrUpdateDeploymentActionModel
{
    public string Name { get; set; }
    public string ActionType { get; set; }
    public int? WorkerPoolId { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsRequired { get; set; }
    public bool CanBeUsedForProjectVersioning { get; set; }
    public List<ActionPropertyModel> Properties { get; set; } = new();
    public List<int> Environments { get; set; } = new();
    public List<int> ExcludedEnvironments { get; set; } = new();
    public List<int> Channels { get; set; } = new();
}
