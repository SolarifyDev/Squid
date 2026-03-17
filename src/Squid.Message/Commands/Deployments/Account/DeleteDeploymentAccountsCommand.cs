using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Account;

[RequiresPermission(Permission.AccountDelete)]
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
