using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Account;

[RequiresPermission(Permission.AccountView)]
public class GetDeploymentAccountsRequest : IPaginatedRequest, ISpaceScoped
{
    public int PageIndex { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int? SpaceId { get; set; }
}

public class GetDeploymentAccountsResponse : SquidResponse<GetDeploymentAccountsResponseData>
{
}

public class GetDeploymentAccountsResponseData
{
    public int Count { get; set; }
    public List<DeploymentAccountDto> DeploymentAccounts { get; set; }
}
