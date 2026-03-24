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
    public string TokenNewValue { get; set; }
    public string Username { get; set; }
    public string PasswordNewValue { get; set; }
    public string ClientCertificateDataNewValue { get; set; }
    public string ClientCertificateKeyDataNewValue { get; set; }
    public string AccessKey { get; set; }
    public string SecretKeyNewValue { get; set; }
    public List<int> EnvironmentIds { get; set; }
}

public class UpdateDeploymentAccountResponse : SquidResponse<UpdateDeploymentAccountResponseData>
{
}

public class UpdateDeploymentAccountResponseData
{
    public DeploymentAccountDto DeploymentAccount { get; set; }
}
