using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.Deployments.Account;

public interface IDeploymentAccountDataProvider : IScopedDependency
{
    Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default);
    Task<DeploymentAccount> AddAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default);
    Task UpdateAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default);
    Task DeleteAccountsAsync(List<DeploymentAccount> accounts, bool forceSave = true, CancellationToken cancellationToken = default);
    Task<List<DeploymentAccount>> GetAccountsByIdsAsync(List<int> ids, CancellationToken cancellationToken = default);
    Task<List<DeploymentAccount>> GetAccountsBySpaceIdAsync(int spaceId, CancellationToken cancellationToken = default);
    Task<(int count, List<DeploymentAccount>)> GetAccountPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns at-rest protection of <see cref="DeploymentAccount.Credentials"/> — the most
/// sensitive secret store in the system (every account type's token / password / cloud
/// secret / SSH private key). This is the single seam every account read and write
/// funnels through, so encrypting here covers the deploy pipeline, the OpenClaw paths,
/// and the account service transparently — mirroring how <c>VariableDataProvider</c>
/// protects the <c>Variable</c> table.
///
/// <para>Read-both / non-breaking: <see cref="IVariableEncryptionService.EncryptAsync"/>
/// always emits the <c>SQUID_ENCRYPTED_V2:</c> envelope; <c>DecryptAsync</c> returns any
/// value lacking that prefix verbatim, so pre-existing plaintext rows still load and
/// upgrade naturally on the next save. Reads use the repository's AsNoTracking query so
/// decrypting the in-memory entity can never be flushed back to the DB as plaintext.</para>
/// </summary>
public class DeploymentAccountDataProvider(IRepository repository, IUnitOfWork unitOfWork, IVariableEncryptionService encryption) : IDeploymentAccountDataProvider
{
    // The V2 envelope derives its key from a random per-payload salt, so the legacy
    // KDF-scope argument (a V1-only deterministic-salt input) is irrelevant for accounts,
    // which only ever write V2. Pass a constant.
    private const int CredentialsKdfScope = 0;

    public async Task<DeploymentAccount> GetAccountByIdAsync(int accountId, CancellationToken cancellationToken = default)
    {
        // QueryNoTracking so the decrypt below mutates a DETACHED entity — a tracked entity
        // would be detected as Modified and a later in-scope SaveChanges (the deploy pipeline
        // shares one DbContext across phases) would flush the plaintext back to the column.
        var account = await repository.QueryNoTracking<DeploymentAccount>(a => a.Id == accountId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return await DecryptCredentialsAsync(account).ConfigureAwait(false);
    }

    public async Task<DeploymentAccount> AddAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        EncryptCredentials(account);

        await repository.InsertAsync(account, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return account;
    }

    public async Task UpdateAccountAsync(DeploymentAccount account, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        EncryptCredentials(account);

        await repository.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAccountsAsync(List<DeploymentAccount> accounts, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(accounts, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAccount>> GetAccountsByIdsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        var accounts = await repository.QueryNoTracking<DeploymentAccount>()
            .Where(a => ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return await DecryptCredentialsAsync(accounts).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAccount>> GetAccountsBySpaceIdAsync(int spaceId, CancellationToken cancellationToken = default)
    {
        var accounts = await repository.QueryNoTracking<DeploymentAccount>()
            .Where(a => a.SpaceId == spaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return await DecryptCredentialsAsync(accounts).ConfigureAwait(false);
    }

    public async Task<(int count, List<DeploymentAccount>)> GetAccountPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.QueryNoTracking<DeploymentAccount>();

        if (spaceId.HasValue)
            query = query.Where(a => a.SpaceId == spaceId.Value);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        var accounts = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, await DecryptCredentialsAsync(accounts).ConfigureAwait(false));
    }

    // Encrypt only if not already an envelope — idempotent, so a re-save (or a value that
    // somehow arrives pre-encrypted) never double-wraps.
    private void EncryptCredentials(DeploymentAccount account)
    {
        if (account == null || string.IsNullOrEmpty(account.Credentials)) return;
        if (encryption.IsValidEncryptedValue(account.Credentials)) return;

        account.Credentials = encryption.EncryptAsync(account.Credentials, CredentialsKdfScope);
    }

    private async Task<DeploymentAccount> DecryptCredentialsAsync(DeploymentAccount account)
    {
        if (account == null || string.IsNullOrEmpty(account.Credentials)) return account;

        // Read-both: a plaintext (unprefixed) value is returned verbatim by DecryptAsync.
        account.Credentials = await encryption.DecryptAsync(account.Credentials, CredentialsKdfScope).ConfigureAwait(false);

        return account;
    }

    private async Task<List<DeploymentAccount>> DecryptCredentialsAsync(List<DeploymentAccount> accounts)
    {
        foreach (var account in accounts)
            await DecryptCredentialsAsync(account).ConfigureAwait(false);

        return accounts;
    }
}
