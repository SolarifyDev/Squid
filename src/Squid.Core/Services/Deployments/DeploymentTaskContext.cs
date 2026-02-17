using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public class DeploymentTaskContext
{
    public Persistence.Entities.Deployments.ServerTask Task { get; set; }

    public Deployment Deployment { get; set; }

    public Persistence.Entities.Deployments.Release Release { get; set; }

    public DeploymentPlanDto Plan { get; set; }

    public List<VariableDto> Variables { get; set; }

    public List<Persistence.Entities.Deployments.Machine> Targets { get; set; } = new();

    public Persistence.Entities.Deployments.Machine Target { get; set; }

    public DeploymentAccount Account { get; set; }

    public string EndpointJson { get; set; }

    public string CommunicationStyle { get; set; }

    public List<DeploymentStepDto> Steps { get; set; }

    public byte[] CalamariPackageBytes { get; set; }

    public List<ActionExecutionResult> ActionResults { get; set; } = new();
}
