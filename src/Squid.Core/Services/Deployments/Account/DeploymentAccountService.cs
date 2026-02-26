using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Deployments.Account;
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
    public async Task<CreateDeploymentAccountResponseData> CreateAsync(
        CreateDeploymentAccountCommand command,
        CancellationToken cancellationToken)
    {
        var entity = new DeploymentAccount
        {
            SpaceId = command.SpaceId,
            Name = command.Name,
            Slug = $"account-{Guid.NewGuid():N}",
            AccountType = command.AccountType,
            Token = command.Token,
            Username = command.Username,
            Password = command.Password,
            ClientCertificateData = command.ClientCertificateData,
            ClientCertificateKeyData = command.ClientCertificateKeyData,
            AccessKey = command.AccessKey,
            SecretKey = command.SecretKey,
        };

        await dataProvider.AddAccountAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CreateDeploymentAccountResponseData
        {
            DeploymentAccount = MapToDto(entity)
        };
    }

    public async Task<UpdateDeploymentAccountResponseData> UpdateAsync(
        UpdateDeploymentAccountCommand command,
        CancellationToken cancellationToken)
    {
        var accounts = await dataProvider.GetAccountsByIdsAsync(new List<int> { command.Id }, cancellationToken).ConfigureAwait(false);
        var entity = accounts.FirstOrDefault();

        if (entity == null)
            throw new Exception("DeploymentAccount not found");

        entity.Name = command.Name;
        entity.AccountType = command.AccountType;
        entity.Username = command.Username;
        entity.AccessKey = command.AccessKey;

        if (command.TokenNewValue != null) entity.Token = command.TokenNewValue;
        if (command.PasswordNewValue != null) entity.Password = command.PasswordNewValue;
        if (command.ClientCertificateDataNewValue != null) entity.ClientCertificateData = command.ClientCertificateDataNewValue;
        if (command.ClientCertificateKeyDataNewValue != null) entity.ClientCertificateKeyData = command.ClientCertificateKeyDataNewValue;
        if (command.SecretKeyNewValue != null) entity.SecretKey = command.SecretKeyNewValue;

        await dataProvider.UpdateAccountAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new UpdateDeploymentAccountResponseData
        {
            DeploymentAccount = MapToDto(entity)
        };
    }

    public async Task<DeleteDeploymentAccountsResponseData> DeleteAsync(
        DeleteDeploymentAccountsCommand command,
        CancellationToken cancellationToken)
    {
        var accounts = await dataProvider.GetAccountsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await dataProvider.DeleteAccountsAsync(accounts, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DeleteDeploymentAccountsResponseData
        {
            FailIds = command.Ids.Except(accounts.Select(a => a.Id)).ToList()
        };
    }

    public async Task<GetDeploymentAccountsResponse> GetAccountsAsync(
        GetDeploymentAccountsRequest request,
        CancellationToken cancellationToken)
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
        return new DeploymentAccountDto
        {
            Id = entity.Id,
            SpaceId = entity.SpaceId,
            Name = entity.Name,
            Slug = entity.Slug,
            AccountType = entity.AccountType,
            Username = entity.Username,
            AccessKey = entity.AccessKey,
            TokenHasValue = !string.IsNullOrEmpty(entity.Token),
            PasswordHasValue = !string.IsNullOrEmpty(entity.Password),
            ClientCertificateDataHasValue = !string.IsNullOrEmpty(entity.ClientCertificateData),
            ClientCertificateKeyDataHasValue = !string.IsNullOrEmpty(entity.ClientCertificateKeyData),
            SecretKeyHasValue = !string.IsNullOrEmpty(entity.SecretKey),
        };
    }
}
