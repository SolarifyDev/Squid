using Squid.Message.Enums.Deployments;

namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentInterruption : IEntity<int>
{
    public int Id { get; set; }
    public int ServerTaskId { get; set; }
    public int DeploymentId { get; set; }
    public InterruptionType InterruptionType { get; set; }
    public int StepDisplayOrder { get; set; }
    public string StepName { get; set; }
    public string ActionName { get; set; }
    public string MachineName { get; set; }
    public string ErrorMessage { get; set; }
    public string FormJson { get; set; }
    public string SubmittedValuesJson { get; set; }
    public string ResponsibleUserId { get; set; }
    public string Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public int SpaceId { get; set; }
}
