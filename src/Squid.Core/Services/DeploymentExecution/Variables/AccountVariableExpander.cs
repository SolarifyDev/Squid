using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

public static class AccountVariableExpander
{
    public static List<VariableDto> Expand(ResolvedAuthenticationAccountData accountData)
    {
        if (accountData == null) return new();

        var creds = DeploymentAccountCredentialsConverter.Deserialize(accountData.AuthenticationAccountType, accountData.CredentialsJson);

        if (creds == null) return new();

        return creds switch
        {
            TokenCredentials tc => ExpandToken(tc),
            UsernamePasswordCredentials up => ExpandUsernamePassword(up),
            ClientCertificateCredentials cc => ExpandClientCertificate(cc),
            AwsRoleCredentials role => ExpandAwsRole(role),
            AwsCredentials aws => ExpandAws(aws),
            AwsOidcCredentials oidc => ExpandAwsOidc(oidc),
            AzureServicePrincipalCredentials az => ExpandAzureServicePrincipal(az),
            AzureOidcCredentials azOidc => ExpandAzureOidc(azOidc),
            GcpCredentials gcp => ExpandGcp(gcp),
            SshKeyPairCredentials ssh => ExpandSsh(ssh),
            _ => new()
        };
    }

    private static List<VariableDto> ExpandToken(TokenCredentials tc) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.Token, tc.Token ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandUsernamePassword(UsernamePasswordCredentials up) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.Username, up.Username ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.Password, up.Password ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandClientCertificate(ClientCertificateCredentials cc) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.ClientCertificateData, cc.ClientCertificateData ?? string.Empty, isSensitive: true),
        EndpointVariableFactory.Make(SpecialVariables.Account.ClientCertificateKeyData, cc.ClientCertificateKeyData ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandAws(AwsCredentials aws) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.AccessKey, aws.AccessKey ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.SecretKey, aws.SecretKey ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandAwsRole(AwsRoleCredentials role) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.AccessKey, role.AccessKey ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.SecretKey, role.SecretKey ?? string.Empty, isSensitive: true),
        EndpointVariableFactory.Make(SpecialVariables.Account.RoleArn, role.RoleArn ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.SessionDuration, role.SessionDuration ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.ExternalId, role.ExternalId ?? string.Empty)
    };

    private static List<VariableDto> ExpandAwsOidc(AwsOidcCredentials oidc) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.RoleArn, oidc.RoleArn ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.WebIdentityToken, oidc.WebIdentityToken ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandAzureServicePrincipal(AzureServicePrincipalCredentials az) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.SubscriptionNumber, az.SubscriptionNumber ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.ClientId, az.ClientId ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.TenantId, az.TenantId ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.AzureKey, az.Key ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandAzureOidc(AzureOidcCredentials azOidc) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.SubscriptionNumber, azOidc.SubscriptionNumber ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.ClientId, azOidc.ClientId ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.TenantId, azOidc.TenantId ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.AzureJwt, azOidc.Jwt ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandGcp(GcpCredentials gcp) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.GcpJsonKey, gcp.JsonKey ?? string.Empty, isSensitive: true)
    };

    private static List<VariableDto> ExpandSsh(SshKeyPairCredentials ssh) => new()
    {
        EndpointVariableFactory.Make(SpecialVariables.Account.Username, ssh.Username ?? string.Empty),
        EndpointVariableFactory.Make(SpecialVariables.Account.SshPrivateKeyFile, ssh.PrivateKeyFile ?? string.Empty, isSensitive: true),
        EndpointVariableFactory.Make(SpecialVariables.Account.SshPassphrase, ssh.PrivateKeyPassphrase ?? string.Empty, isSensitive: true)
    };
}
