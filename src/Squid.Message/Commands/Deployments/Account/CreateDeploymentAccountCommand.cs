using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Account;

[RequiresPermission(Permission.AccountCreate)]
public class CreateDeploymentAccountCommand : ICommand
{
    public int SpaceId { get; set; }
    public string Name { get; set; }
    public AccountType AccountType { get; set; }
    public string Token { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public string ClientCertificateData { get; set; }
    public string ClientCertificateKeyData { get; set; }
    public string AccessKey { get; set; }
    public string SecretKey { get; set; }
}

public class CreateDeploymentAccountResponse : SquidResponse<CreateDeploymentAccountResponseData>
{
}

public class CreateDeploymentAccountResponseData
{
    public DeploymentAccountDto DeploymentAccount { get; set; }
}
