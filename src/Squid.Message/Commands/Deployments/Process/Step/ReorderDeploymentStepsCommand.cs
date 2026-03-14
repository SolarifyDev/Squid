using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process.Step;

public class ReorderDeploymentStepsCommand : ICommand
{
    public int ProcessId { get; set; }

    public List<StepOrderItem> StepOrders { get; set; } = new();
}

public class StepOrderItem
{
    public int StepId { get; set; }

    public int StepOrder { get; set; }
}

public class ReorderDeploymentStepsResponse : SquidResponse<List<DeploymentStepDto>>
{
}
