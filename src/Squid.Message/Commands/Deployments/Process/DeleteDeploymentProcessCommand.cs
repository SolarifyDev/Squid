using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process;

public class DeleteDeploymentProcessCommand : ICommand
{
    public int Id { get; set; }
}

public class DeleteDeploymentProcessResponse : SquidResponse<DeleteDeploymentProcessResponseData>
{
}

public class DeleteDeploymentProcessResponseData
{
    public string Message { get; set; }
}
