using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class AccountVariableExpanderTests
{
    // === Token ===

    [Fact]
    public void Expand_TokenCredentials_ReturnsTokenVariable()
    {
        var data = MakeAccountData(AccountType.Token, new TokenCredentials { Token = "my-token" });

        var vars = AccountVariableExpander.Expand(data);

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.Token && v.Value == "my-token" && v.IsSensitive);
    }

    // === UsernamePassword ===

    [Fact]
    public void Expand_UsernamePasswordCredentials_ReturnsUsernameAndPassword()
    {
        var data = MakeAccountData(AccountType.UsernamePassword, new UsernamePasswordCredentials { Username = "admin", Password = "s3cret" });

        var vars = AccountVariableExpander.Expand(data);

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.Username && v.Value == "admin" && !v.IsSensitive);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.Password && v.Value == "s3cret" && v.IsSensitive);
    }

    // === ClientCertificate ===

    [Fact]
    public void Expand_ClientCertificateCredentials_ReturnsCertDataAndKeyData()
    {
        var data = MakeAccountData(AccountType.ClientCertificate, new ClientCertificateCredentials { ClientCertificateData = "cert-data", ClientCertificateKeyData = "key-data" });

        var vars = AccountVariableExpander.Expand(data);

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.ClientCertificateData && v.Value == "cert-data" && v.IsSensitive);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.ClientCertificateKeyData && v.Value == "key-data" && v.IsSensitive);
    }

    // === AwsCredentials ===

    [Fact]
    public void Expand_AwsCredentials_ReturnsAccessKeyAndSecretKey()
    {
        var data = MakeAccountData(AccountType.AmazonWebServicesAccount, new AwsCredentials { AccessKey = "AKIA123", SecretKey = "secret" });

        var vars = AccountVariableExpander.Expand(data);

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.AccessKey && v.Value == "AKIA123" && !v.IsSensitive);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.SecretKey && v.Value == "secret" && v.IsSensitive);
    }

    // === AwsRoleCredentials ===

    [Fact]
    public void Expand_AwsRoleCredentials_ReturnsAllRoleFields()
    {
        var data = MakeAccountData(AccountType.AmazonWebServicesRoleAccount, new AwsRoleCredentials
        {
            AccessKey = "AKIA456", SecretKey = "role-secret", RoleArn = "arn:aws:iam::123:role/test", SessionDuration = "3600", ExternalId = "ext-id"
        });

        var vars = AccountVariableExpander.Expand(data);

        vars.Count.ShouldBe(5);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.AccessKey && v.Value == "AKIA456");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.SecretKey && v.Value == "role-secret" && v.IsSensitive);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.RoleArn && v.Value == "arn:aws:iam::123:role/test");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.SessionDuration && v.Value == "3600");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.ExternalId && v.Value == "ext-id");
    }

    // === AwsOidcCredentials ===

    [Fact]
    public void Expand_AwsOidcCredentials_ReturnsRoleArnAndWebIdentityToken()
    {
        var data = MakeAccountData(AccountType.AmazonWebServicesOidcAccount, new AwsOidcCredentials { RoleArn = "arn:aws:iam::456:role/oidc", WebIdentityToken = "jwt-token" });

        var vars = AccountVariableExpander.Expand(data);

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.RoleArn && v.Value == "arn:aws:iam::456:role/oidc");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.WebIdentityToken && v.Value == "jwt-token" && v.IsSensitive);
    }

    // === AzureServicePrincipal ===

    [Fact]
    public void Expand_AzureServicePrincipalCredentials_ReturnsAllFields()
    {
        var data = MakeAccountData(AccountType.AzureServicePrincipal, new AzureServicePrincipalCredentials
        {
            SubscriptionNumber = "sub-123", ClientId = "client-id", TenantId = "tenant-id", Key = "az-key"
        });

        var vars = AccountVariableExpander.Expand(data);

        vars.Count.ShouldBe(4);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.SubscriptionNumber && v.Value == "sub-123");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.ClientId && v.Value == "client-id");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.TenantId && v.Value == "tenant-id");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.AzureKey && v.Value == "az-key" && v.IsSensitive);
    }

    // === AzureOidc ===

    [Fact]
    public void Expand_AzureOidcCredentials_ReturnsAllFields()
    {
        var data = MakeAccountData(AccountType.AzureOidc, new AzureOidcCredentials
        {
            SubscriptionNumber = "sub-456", ClientId = "oidc-client", TenantId = "oidc-tenant", Jwt = "az-jwt"
        });

        var vars = AccountVariableExpander.Expand(data);

        vars.Count.ShouldBe(4);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.SubscriptionNumber && v.Value == "sub-456");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.ClientId && v.Value == "oidc-client");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.TenantId && v.Value == "oidc-tenant");
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.AzureJwt && v.Value == "az-jwt" && v.IsSensitive);
    }

    // === GcpCredentials ===

    [Fact]
    public void Expand_GcpCredentials_ReturnsJsonKey()
    {
        var data = MakeAccountData(AccountType.GoogleCloudAccount, new GcpCredentials { JsonKey = "{\"type\":\"service_account\"}" });

        var vars = AccountVariableExpander.Expand(data);

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.GcpJsonKey && v.Value == "{\"type\":\"service_account\"}" && v.IsSensitive);
    }

    // === SshKeyPair ===

    [Fact]
    public void Expand_SshKeyPairCredentials_ReturnsAllFields()
    {
        var data = MakeAccountData(AccountType.SshKeyPair, new SshKeyPairCredentials { Username = "deploy", PrivateKeyFile = "key-content", PrivateKeyPassphrase = "pass" });

        var vars = AccountVariableExpander.Expand(data);

        vars.Count.ShouldBe(3);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.Username && v.Value == "deploy" && !v.IsSensitive);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.SshPrivateKeyFile && v.Value == "key-content" && v.IsSensitive);
        vars.ShouldContain(v => v.Name == SpecialVariables.Account.SshPassphrase && v.Value == "pass" && v.IsSensitive);
    }

    // === Null/empty edge cases ===

    [Fact]
    public void Expand_NullAccountData_ReturnsEmptyList()
    {
        var vars = AccountVariableExpander.Expand(null);

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void Expand_NullCredentialsJson_ReturnsEmptyList()
    {
        var data = new ResolvedAuthenticationAccountData { AuthenticationAccountType = AccountType.Token, CredentialsJson = null };

        var vars = AccountVariableExpander.Expand(data);

        vars.ShouldBeEmpty();
    }

    private static ResolvedAuthenticationAccountData MakeAccountData(AccountType type, object credentials)
    {
        return new ResolvedAuthenticationAccountData
        {
            AuthenticationAccountType = type,
            CredentialsJson = JsonSerializer.Serialize(credentials, credentials.GetType())
        };
    }
}
