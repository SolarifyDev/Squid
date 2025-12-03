namespace Squid.Message.Models.Deployments.Process;

public class DeploymentActionDto
{
    public int Id { get; set; }

    public int StepId { get; set; }

    public int ActionOrder { get; set; }

    public string Name { get; set; }

    public string ActionType { get; set; }

    public int? WorkerPoolId { get; set; }

    public int? FeedId { get; set; }

    public string PackageId { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsRequired { get; set; }

    public bool CanBeUsedForProjectVersioning { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<DeploymentActionPropertyDto> Properties { get; set; } = new List<DeploymentActionPropertyDto>();
}
