using System.Text.Json;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.Core.Services.Deployments.Account;

public static class DeploymentAccountCredentialsConverter
{
    public static string Serialize(object credentials)
    {
        if (credentials == null) return null;

        return JsonSerializer.Serialize(credentials, credentials.GetType());
    }

    public static object Deserialize(AccountType accountType, string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        return accountType switch
        {
            AccountType.Token => JsonSerializer.Deserialize<TokenCredentials>(json),
            AccountType.UsernamePassword => JsonSerializer.Deserialize<UsernamePasswordCredentials>(json),
            AccountType.ClientCertificate => JsonSerializer.Deserialize<ClientCertificateCredentials>(json),
            AccountType.AmazonWebServicesAccount => JsonSerializer.Deserialize<AwsCredentials>(json),
            AccountType.AmazonWebServicesRoleAccount => JsonSerializer.Deserialize<AwsCredentials>(json),
            AccountType.SshKeyPair => JsonSerializer.Deserialize<SshKeyPairCredentials>(json),
            AccountType.AzureServicePrincipal => JsonSerializer.Deserialize<AzureServicePrincipalCredentials>(json),
            AccountType.AzureOidc => JsonSerializer.Deserialize<AzureOidcCredentials>(json),
            AccountType.GoogleCloudAccount => JsonSerializer.Deserialize<GcpCredentials>(json),
            AccountType.AmazonWebServicesOidcAccount => JsonSerializer.Deserialize<AwsOidcCredentials>(json),
            _ => null
        };
    }
}
