using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Account;

public class DeleteDeploymentAccountsCommand : ICommand
{
    public List<int> Ids { get; set; }
}

public class DeleteDeploymentAccountsResponse : SquidResponse<DeleteDeploymentAccountsResponseData>
{
}

public class DeleteDeploymentAccountsResponseData
{
    public List<int> FailIds { get; set; }
}
