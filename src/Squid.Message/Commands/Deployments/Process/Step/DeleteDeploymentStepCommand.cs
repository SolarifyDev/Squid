using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process.Step;

public class DeleteDeploymentStepCommand : ICommand
{
    public List<int> Ids { get; set; } = new List<int>();
}

public class DeleteDeploymentStepResponse : SquidResponse<DeleteDeploymentStepResponseData>
{
}

public class DeleteDeploymentStepResponseData
{
    public List<int> FailIds { get; set; } = new List<int>();
}

