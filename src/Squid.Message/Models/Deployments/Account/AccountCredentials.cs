namespace Squid.Message.Models.Deployments.Account;

public class TokenCredentials
{
    public string Token { get; set; }
}

public class UsernamePasswordCredentials
{
    public string Username { get; set; }
    public string Password { get; set; }
}

public class ClientCertificateCredentials
{
    public string ClientCertificateData { get; set; }
    public string ClientCertificateKeyData { get; set; }
}

public class AwsCredentials
{
    public string AccessKey { get; set; }
    public string SecretKey { get; set; }
}

public class SshKeyPairCredentials
{
    public string Username { get; set; }
    public string PrivateKeyFile { get; set; }
    public string PrivateKeyPassphrase { get; set; }
}

public class AzureServicePrincipalCredentials
{
    public string SubscriptionNumber { get; set; }
    public string ClientId { get; set; }
    public string TenantId { get; set; }
    public string Key { get; set; }
}
