using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Message.Models.Deployments.Execution;

public class ActionExecutionContext
{
    public DeploymentStepDto Step { get; set; }

    public DeploymentActionDto Action { get; set; }

    public List<VariableDto> Variables { get; set; }

    public string ReleaseVersion { get; set; }
}
