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
        var credentials = BuildCredentials(command.AccountType, command);

        var entity = new DeploymentAccount
        {
            SpaceId = command.SpaceId,
            Name = command.Name,
            Slug = $"account-{Guid.NewGuid():N}",
            AccountType = command.AccountType,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(credentials)
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

        var existing = DeploymentAccountCredentialsConverter.Deserialize(entity.AccountType, entity.Credentials);
        
        var merged = MergeCredentials(command, existing);
        
        entity.Credentials = DeploymentAccountCredentialsConverter.Serialize(merged);

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
            Username = (creds as UsernamePasswordCredentials)?.Username ?? (creds as SshKeyPairCredentials)?.Username,
            AccessKey = (creds as AwsCredentials)?.AccessKey,
            TokenHasValue = creds is TokenCredentials tc && !string.IsNullOrEmpty(tc.Token),
            PasswordHasValue = creds is UsernamePasswordCredentials up && !string.IsNullOrEmpty(up.Password),
            ClientCertificateDataHasValue = creds is ClientCertificateCredentials cert && !string.IsNullOrEmpty(cert.ClientCertificateData),
            ClientCertificateKeyDataHasValue = creds is ClientCertificateCredentials certKey && !string.IsNullOrEmpty(certKey.ClientCertificateKeyData),
            SecretKeyHasValue = creds is AwsCredentials aws && !string.IsNullOrEmpty(aws.SecretKey),
        };
    }

    private static object BuildCredentials(AccountType accountType, CreateDeploymentAccountCommand command)
    {
        return accountType switch
        {
            AccountType.Token => new TokenCredentials { Token = command.Token },
            AccountType.UsernamePassword => new UsernamePasswordCredentials
            {
                Username = command.Username,
                Password = command.Password
            },
            AccountType.ClientCertificate => new ClientCertificateCredentials
            {
                ClientCertificateData = command.ClientCertificateData,
                ClientCertificateKeyData = command.ClientCertificateKeyData
            },
            AccountType.AmazonWebServicesAccount or AccountType.AmazonWebServicesRoleAccount => new AwsCredentials
            {
                AccessKey = command.AccessKey,
                SecretKey = command.SecretKey
            },
            _ => null
        };
    }

    private static object MergeCredentials(UpdateDeploymentAccountCommand command, object existing)
    {
        return command.AccountType switch
        {
            AccountType.Token => new TokenCredentials
            {
                Token = command.TokenNewValue ?? (existing as TokenCredentials)?.Token
            },
            AccountType.UsernamePassword => new UsernamePasswordCredentials
            {
                Username = command.Username ?? (existing as UsernamePasswordCredentials)?.Username,
                Password = command.PasswordNewValue ?? (existing as UsernamePasswordCredentials)?.Password
            },
            AccountType.ClientCertificate => new ClientCertificateCredentials
            {
                ClientCertificateData = command.ClientCertificateDataNewValue ?? (existing as ClientCertificateCredentials)?.ClientCertificateData,
                ClientCertificateKeyData = command.ClientCertificateKeyDataNewValue ?? (existing as ClientCertificateCredentials)?.ClientCertificateKeyData
            },
            AccountType.AmazonWebServicesAccount or AccountType.AmazonWebServicesRoleAccount => new AwsCredentials
            {
                AccessKey = command.AccessKey ?? (existing as AwsCredentials)?.AccessKey,
                SecretKey = command.SecretKeyNewValue ?? (existing as AwsCredentials)?.SecretKey
            },
            _ => existing
        };
    }
}
