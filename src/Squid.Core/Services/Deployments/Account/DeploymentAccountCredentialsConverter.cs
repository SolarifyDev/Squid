using System.Text.Json;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.Core.Services.Deployments.Account;

public static class DeploymentAccountCredentialsConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(object credentials)
    {
        if (credentials == null) return null;

        return JsonSerializer.Serialize(credentials, credentials.GetType(), JsonOptions);
    }

    public static object Deserialize(AccountType accountType, string json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        var type = GetCredentialsType(accountType);

        return type == null ? null : JsonSerializer.Deserialize(json, type, JsonOptions);
    }

    public static object Deserialize(AccountType accountType, JsonElement? json)
    {
        if (json == null) return null;

        var type = GetCredentialsType(accountType);

        return type == null ? null : json.Value.Deserialize(type, JsonOptions);
    }

    private static Type GetCredentialsType(AccountType accountType)
    {
        return accountType switch
        {
            AccountType.Token => typeof(TokenCredentials),
            AccountType.UsernamePassword => typeof(UsernamePasswordCredentials),
            AccountType.ClientCertificate => typeof(ClientCertificateCredentials),
            AccountType.AmazonWebServicesAccount => typeof(AwsCredentials),
            AccountType.AmazonWebServicesRoleAccount => typeof(AwsRoleCredentials),
            AccountType.SshKeyPair => typeof(SshKeyPairCredentials),
            AccountType.AzureServicePrincipal => typeof(AzureServicePrincipalCredentials),
            AccountType.AzureOidc => typeof(AzureOidcCredentials),
            AccountType.GoogleCloudAccount => typeof(GcpCredentials),
            AccountType.AmazonWebServicesOidcAccount => typeof(AwsOidcCredentials),
            _ => null
        };
    }
}
