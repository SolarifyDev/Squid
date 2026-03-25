using System.Text.Json;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Account;

[RequiresPermission(Permission.AccountCreate)]
public class CreateDeploymentAccountCommand : ICommand, ISpaceScoped
{
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public string Name { get; set; }
    public AccountType AccountType { get; set; }
    public JsonElement? Credentials { get; set; }
    public List<int> EnvironmentIds { get; set; }
}

public class CreateDeploymentAccountResponse : SquidResponse<CreateDeploymentAccountResponseData>
{
}

public class CreateDeploymentAccountResponseData
{
    public DeploymentAccountDto DeploymentAccount { get; set; }
}
