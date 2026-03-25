using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Requests.Deployments.Account;

namespace Squid.Core.Services.Deployments.Account;

public interface IDeploymentAccountService : IScopedDependency
{
    Task<CreateDeploymentAccountResponseData> CreateAsync(CreateDeploymentAccountCommand command, CancellationToken cancellationToken);
    Task<UpdateDeploymentAccountResponseData> UpdateAsync(UpdateDeploymentAccountCommand command, CancellationToken cancellationToken);
    Task<DeleteDeploymentAccountsResponseData> DeleteAsync(DeleteDeploymentAccountsCommand command, CancellationToken cancellationToken);
    Task<GetDeploymentAccountsResponse> GetAccountsAsync(GetDeploymentAccountsRequest request, CancellationToken cancellationToken);
}

public class DeploymentAccountService(IDeploymentAccountDataProvider dataProvider) : IDeploymentAccountService
{
    public async Task<CreateDeploymentAccountResponseData> CreateAsync(CreateDeploymentAccountCommand command, CancellationToken cancellationToken)
    {
        var credentials = DeploymentAccountCredentialsConverter.Deserialize(command.AccountType, command.Credentials);

        var entity = new DeploymentAccount
        {
            SpaceId = command.SpaceId,
            Name = command.Name,
            Slug = $"account-{Guid.NewGuid():N}",
            AccountType = command.AccountType,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(credentials),
            EnvironmentIds = SerializeEnvironmentIds(command.EnvironmentIds)
        };

        await dataProvider.AddAccountAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CreateDeploymentAccountResponseData
        {
            DeploymentAccount = MapToDto(entity)
        };
    }

    public async Task<UpdateDeploymentAccountResponseData> UpdateAsync(UpdateDeploymentAccountCommand command, CancellationToken cancellationToken)
    {
        var accounts = await dataProvider.GetAccountsByIdsAsync(new List<int> { command.Id }, cancellationToken).ConfigureAwait(false);
        
        var entity = accounts.FirstOrDefault();

        if (entity == null)
            throw new Exception("DeploymentAccount not found");

        entity.Name = command.Name;
        entity.AccountType = command.AccountType;

        if (command.EnvironmentIds != null)
            entity.EnvironmentIds = SerializeEnvironmentIds(command.EnvironmentIds);

        var incoming = DeploymentAccountCredentialsConverter.Deserialize(command.AccountType, command.Credentials);

        entity.Credentials = incoming != null
            ? DeploymentAccountCredentialsConverter.Serialize(incoming)
            : entity.Credentials;

        await dataProvider.UpdateAccountAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new UpdateDeploymentAccountResponseData
        {
            DeploymentAccount = MapToDto(entity)
        };
    }

    public async Task<DeleteDeploymentAccountsResponseData> DeleteAsync(DeleteDeploymentAccountsCommand command, CancellationToken cancellationToken)
    {
        var accounts = await dataProvider.GetAccountsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await dataProvider.DeleteAccountsAsync(accounts, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DeleteDeploymentAccountsResponseData
        {
            FailIds = command.Ids.Except(accounts.Select(a => a.Id)).ToList()
        };
    }

    public async Task<GetDeploymentAccountsResponse> GetAccountsAsync(GetDeploymentAccountsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await dataProvider.GetAccountPagingAsync(
            request.SpaceId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetDeploymentAccountsResponse
        {
            Data = new GetDeploymentAccountsResponseData
            {
                Count = count,
                DeploymentAccounts = data.Select(MapToDto).ToList()
            }
        };
    }

    private static DeploymentAccountDto MapToDto(DeploymentAccount entity)
    {
        var creds = DeploymentAccountCredentialsConverter.Deserialize(entity.AccountType, entity.Credentials);

        return new DeploymentAccountDto
        {
            Id = entity.Id,
            SpaceId = entity.SpaceId,
            Name = entity.Name,
            Slug = entity.Slug,
            AccountType = entity.AccountType,
            Credentials = BuildCredentialsSummary(entity.AccountType, creds),
            EnvironmentIds = DeserializeEnvironmentIds(entity.EnvironmentIds)
        };
    }

    internal static object BuildCredentialsSummary(AccountType accountType, object creds)
    {
        return accountType switch
        {
            AccountType.Token => new TokenCredentialsSummary
            {
                TokenHasValue = creds is TokenCredentials tc && !string.IsNullOrEmpty(tc.Token)
            },
            AccountType.UsernamePassword => new UsernamePasswordCredentialsSummary
            {
                Username = (creds as UsernamePasswordCredentials)?.Username,
                PasswordHasValue = creds is UsernamePasswordCredentials up && !string.IsNullOrEmpty(up.Password)
            },
            AccountType.ClientCertificate => new ClientCertificateCredentialsSummary
            {
                CertificateDataHasValue = creds is ClientCertificateCredentials cert && !string.IsNullOrEmpty(cert.ClientCertificateData),
                CertificateKeyDataHasValue = creds is ClientCertificateCredentials certKey && !string.IsNullOrEmpty(certKey.ClientCertificateKeyData)
            },
            AccountType.AmazonWebServicesAccount or AccountType.AmazonWebServicesRoleAccount => new AwsCredentialsSummary
            {
                AccessKey = (creds as AwsCredentials)?.AccessKey,
                SecretKeyHasValue = creds is AwsCredentials aws && !string.IsNullOrEmpty(aws.SecretKey)
            },
            AccountType.SshKeyPair => new SshKeyPairCredentialsSummary
            {
                Username = (creds as SshKeyPairCredentials)?.Username,
                PrivateKeyFileHasValue = creds is SshKeyPairCredentials ssh && !string.IsNullOrEmpty(ssh.PrivateKeyFile),
                PassphraseHasValue = creds is SshKeyPairCredentials sshP && !string.IsNullOrEmpty(sshP.PrivateKeyPassphrase)
            },
            AccountType.AzureServicePrincipal => new AzureServicePrincipalCredentialsSummary
            {
                SubscriptionNumber = (creds as AzureServicePrincipalCredentials)?.SubscriptionNumber,
                ClientId = (creds as AzureServicePrincipalCredentials)?.ClientId,
                TenantId = (creds as AzureServicePrincipalCredentials)?.TenantId,
                KeyHasValue = creds is AzureServicePrincipalCredentials az && !string.IsNullOrEmpty(az.Key)
            },
            AccountType.AzureOidc => new AzureOidcCredentialsSummary
            {
                SubscriptionNumber = (creds as AzureOidcCredentials)?.SubscriptionNumber,
                ClientId = (creds as AzureOidcCredentials)?.ClientId,
                TenantId = (creds as AzureOidcCredentials)?.TenantId,
                JwtHasValue = creds is AzureOidcCredentials oidc && !string.IsNullOrEmpty(oidc.Jwt)
            },
            AccountType.GoogleCloudAccount => new GcpCredentialsSummary
            {
                JsonKeyHasValue = creds is GcpCredentials gcp && !string.IsNullOrEmpty(gcp.JsonKey)
            },
            _ => null
        };
    }

    internal static string SerializeEnvironmentIds(List<int> environmentIds)
    {
        if (environmentIds == null || environmentIds.Count == 0) return null;

        return JsonSerializer.Serialize(environmentIds);
    }

    internal static List<int> DeserializeEnvironmentIds(string json)
    {
        if (string.IsNullOrEmpty(json)) return new();

        try { return JsonSerializer.Deserialize<List<int>>(json); }
        catch { return new(); }
    }
}
