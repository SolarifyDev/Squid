using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Account;

public class DeploymentAccountDto : IBaseModel
{
    public int Id { get; set; }
    public int SpaceId { get; set; }
    public string Name { get; set; }
    public string Slug { get; set; }
    public AccountType AccountType { get; set; }
    public string Username { get; set; }
    public string AccessKey { get; set; }
    public bool TokenHasValue { get; set; }
    public bool PasswordHasValue { get; set; }
    public bool ClientCertificateDataHasValue { get; set; }
    public bool ClientCertificateKeyDataHasValue { get; set; }
    public bool SecretKeyHasValue { get; set; }
}
