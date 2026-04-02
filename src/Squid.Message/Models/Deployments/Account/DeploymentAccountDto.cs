using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Account;

public class DeploymentAccountDto : IBaseModel
{
    public int Id { get; set; }
    public int SpaceId { get; set; }
    public string Name { get; set; }
    public string Slug { get; set; }
    public AccountType AccountType { get; set; }
    public object Credentials { get; set; }
    public List<int> EnvironmentIds { get; set; }
}

public class TokenCredentialsSummary
{
    public bool TokenHasValue { get; set; }
}

public class UsernamePasswordCredentialsSummary
{
    public string Username { get; set; }
    public bool PasswordHasValue { get; set; }
}

public class ClientCertificateCredentialsSummary
{
    public bool CertificateDataHasValue { get; set; }
    public bool CertificateKeyDataHasValue { get; set; }
}

public class AwsCredentialsSummary
{
    public string AccessKey { get; set; }
    public bool SecretKeyHasValue { get; set; }
}

public class SshKeyPairCredentialsSummary
{
    public string Username { get; set; }
    public bool PrivateKeyFileHasValue { get; set; }
    public bool PassphraseHasValue { get; set; }
}

public class AzureServicePrincipalCredentialsSummary
{
    public string SubscriptionNumber { get; set; }
    public string ClientId { get; set; }
    public string TenantId { get; set; }
    public bool KeyHasValue { get; set; }
}

public class AzureOidcCredentialsSummary
{
    public string SubscriptionNumber { get; set; }
    public string ClientId { get; set; }
    public string TenantId { get; set; }
    public bool JwtHasValue { get; set; }
}

public class GcpCredentialsSummary
{
    public bool JsonKeyHasValue { get; set; }
}

public class OpenClawGatewayCredentialsSummary
{
    public bool GatewayTokenHasValue { get; set; }
    public bool HooksTokenHasValue { get; set; }
}
