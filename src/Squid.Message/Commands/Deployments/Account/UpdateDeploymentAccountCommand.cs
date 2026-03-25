using System.Text.Json;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Account;

[RequiresPermission(Permission.AccountEdit)]
public class UpdateDeploymentAccountCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
    public string Name { get; set; }
    public AccountType AccountType { get; set; }
    public JsonElement? Credentials { get; set; }
    public List<int> EnvironmentIds { get; set; }
}

public class UpdateDeploymentAccountResponse : SquidResponse<UpdateDeploymentAccountResponseData>
{
}

public class UpdateDeploymentAccountResponseData
{
    public DeploymentAccountDto DeploymentAccount { get; set; }
}
