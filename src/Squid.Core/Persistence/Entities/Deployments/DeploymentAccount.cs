using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentAccount : IEntity<int>
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string EnvironmentId { get; set; }

    public AccountType AccountType { get; set; }

    public string Token { get; set; }

    // UsernamePassword
    public string Username { get; set; }

    public string Password { get; set; }

    // SshKeyPair
    public string PrivateKeyFile { get; set; }

    public string PrivateKeyPassphrase { get; set; }

    // ClientCertificate
    public string ClientCertificateData { get; set; }

    public string ClientCertificateKeyData { get; set; }

    // AWS
    public string AccessKey { get; set; }

    public string SecretKey { get; set; }

    public string AssumeRoleArn { get; set; }

    // Azure
    public string SubscriptionNumber { get; set; }

    public string ClientId { get; set; }

    public string TenantId { get; set; }

    public string Key { get; set; }

    // Generic extension
    public string Json { get; set; }
}