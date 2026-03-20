using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public class StepActionContext
{
    public int ServerTaskId { get; init; }
    public int DeploymentId { get; init; }
    public int SpaceId { get; init; }
    public DeploymentStepDto Step { get; init; }
    public DeploymentActionDto Action { get; init; }
    public List<VariableDto> Variables { get; init; }
    public string ReleaseVersion { get; init; }
    public int StepDisplayOrder { get; init; }
    public int ActionSortOrder { get; init; }

    // Populated for step-level handlers that need target context
    public DeploymentTaskContext DeploymentContext { get; init; }
}
