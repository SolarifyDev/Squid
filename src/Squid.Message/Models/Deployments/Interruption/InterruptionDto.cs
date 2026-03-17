using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments.Interruption;

public class InterruptionDto
{
    public int Id { get; set; }
    public int ServerTaskId { get; set; }
    public InterruptionType InterruptionType { get; set; }
    public string StepName { get; set; }
    public string ActionName { get; set; }
    public string MachineName { get; set; }
    public InterruptionForm Form { get; set; }
    public string ResponsibleUserId { get; set; }
    public string ResponsibleTeamIds { get; set; }
    public bool CanTakeResponsibility { get; set; }
    public bool IsPending { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
}
