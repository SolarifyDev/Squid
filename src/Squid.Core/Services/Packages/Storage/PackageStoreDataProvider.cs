using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// Protects <see cref="PackageStore.Secret"/> the same way <c>ExternalFeedDataProvider</c> protects a feed password: the V2 envelope on every write,
/// read-both on every read. Every read here is no-tracking, so decrypting the entity in memory can never be flushed back as plaintext by a later
/// SaveChanges in the same scope. Writes that can race another change (update, set default, delete) are shaped so the loser fails with a specific
/// exception, or cannot hurt the winner at all.
/// </summary>
public class PackageStoreDataProvider(IUnitOfWork unitOfWork, IRepository repository, IAtRestSecretProtector protector) : IPackageStoreDataProvider
{
    // The V2 envelope derives its key from a random per-payload salt, so the legacy KDF-scope argument is irrelevant here.
    private const int SecretKdfScope = 0;

    private static readonly string[] RaceSqlStates = [PostgresErrorCodes.UniqueViolation, PostgresErrorCodes.DeadlockDetected, PostgresErrorCodes.SerializationFailure];

    public async Task<List<PackageStore>> GetAllAsync(CancellationToken ct)
    {
        var stores = await repository.QueryNoTracking<PackageStore>().OrderBy(s => s.Name).ToListAsync(ct).ConfigureAwait(false);

        foreach (var store in stores)
            await DecryptSecretAsync(store).ConfigureAwait(false);

        return stores;
    }

    public async Task<PackageStore> GetByIdAsync(int id, CancellationToken ct)
    {
        var store = await repository.QueryNoTracking<PackageStore>(s => s.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return await DecryptSecretAsync(store).ConfigureAwait(false);
    }

    public async Task<PackageStore> GetDefaultAsync(CancellationToken ct)
    {
        var store = await repository.QueryNoTracking<PackageStore>(s => s.IsDefault).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return await DecryptSecretAsync(store).ConfigureAwait(false);
    }

    public Task<bool> NameExistsAsync(string name, int? exceptId, CancellationToken ct)
    {
        var lowered = name.ToLower();

        return repository.QueryNoTracking<PackageStore>(s => s.Name.ToLower() == lowered && s.Id != exceptId).AnyAsync(ct);
    }

    public async Task AddAsync(PackageStore store, CancellationToken ct)
    {
        store.Secret = protector.Protect(store.Secret, SecretKdfScope);

        await repository.InsertAsync(store, ct).ConfigureAwait(false);

        try
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            repository.Detach(store);

            throw new PackageStoreNameConflictException(store.Name);
        }
    }

    // The row is read tracked at the moment of the write and only the three settings are assigned, so EF writes only those columns: IsDefault and the
    // last test result are never part of the UPDATE, and an edit that raced a set-default or a test cannot undo it.
    public async Task UpdateAsync(PackageStore store, bool resetTestResult, CancellationToken ct)
    {
        var current = await repository.Query<PackageStore>(s => s.Id == store.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (current == null)
            throw new PackageStoreNotFoundException(store.Id);

        current.Name = store.Name;
        current.Properties = store.Properties;
        current.Secret = protector.Protect(store.Secret, SecretKdfScope);

        if (resetTestResult) ClearTestResult(current);

        await SaveEditAsync(current, ct).ConfigureAwait(false);
    }

    // A tracked delete (not ExecuteDelete) so the save is audited. The row is locked first: a concurrent SetDefault must wait for this transaction,
    // so a store that becomes the default can never be deleted underneath it.
    public async Task<bool> DeleteIfNotDefaultAsync(int id, CancellationToken ct)
    {
        await using var transaction = await repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var rows = await repository.FromSqlRaw<PackageStore>("SELECT * FROM package_store WHERE id = {0} FOR UPDATE", id).ToListAsync(ct).ConfigureAwait(false);
        var store = rows.SingleOrDefault();

        if (store == null || store.IsDefault) return false;

        await repository.DeleteAsync(store, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return true;
    }

    // Every row is locked, in id order, before anything changes: two set-defaults (or a set-default and a delete) queue up instead of deadlocking or
    // tripping the single-default index. A loser that still hits a race is reported as a concurrent change, never as a server error.
    public async Task<bool> SetDefaultAsync(int id, CancellationToken ct)
    {
        await using var transaction = await repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            var rows = await repository.FromSqlRaw<PackageStore>("SELECT * FROM package_store ORDER BY id FOR UPDATE").ToListAsync(ct).ConfigureAwait(false);
            var target = rows.FirstOrDefault(r => r.Id == id);

            if (target == null) return false;

            await ClearOtherDefaultsAsync(rows, id, ct).ConfigureAwait(false);

            target.IsDefault = true;

            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (IsRace(ex))
        {
            repository.ClearChangeTracker();

            throw new PackageStoreConcurrentChangeException(id);
        }
    }

    public Task RecordTestResultAsync(int id, bool success, string message, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        return repository.ExecuteUpdateAsync<PackageStore>(s => s.Id == id, set => set
            .SetProperty(s => s.LastTestedDate, now)
            .SetProperty(s => s.LastTestSucceeded, success)
            .SetProperty(s => s.LastTestMessage, message), ct);
    }

    // The unique index allows one default row, so the old default is cleared (and saved) before the new one is set.
    private async Task ClearOtherDefaultsAsync(List<PackageStore> rows, int keepId, CancellationToken ct)
    {
        foreach (var store in rows.Where(r => r.IsDefault && r.Id != keepId))
            store.IsDefault = false;

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SaveEditAsync(PackageStore current, CancellationToken ct)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            repository.Detach(current);

            throw new PackageStoreNotFoundException(current.Id);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            repository.Detach(current);

            throw new PackageStoreNameConflictException(current.Name);
        }
    }

    private static void ClearTestResult(PackageStore store)
    {
        store.LastTestedDate = null;
        store.LastTestSucceeded = null;
        store.LastTestMessage = null;
    }

    private static bool IsNameConflict(DbUpdateException ex) => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: not null } pg && pg.ConstraintName.Contains("name");

    private static bool IsRace(Exception ex)
    {
        return ex is DbUpdateConcurrencyException
            || ex is DbUpdateException { InnerException: PostgresException inner } && RaceSqlStates.Contains(inner.SqlState)
            || ex is PostgresException direct && RaceSqlStates.Contains(direct.SqlState);
    }

    // Read-both: an unprefixed value is returned verbatim by Unprotect.
    private async Task<PackageStore> DecryptSecretAsync(PackageStore store)
    {
        if (store == null || string.IsNullOrEmpty(store.Secret)) return store;

        store.Secret = await protector.UnprotectAsync(store.Secret, SecretKdfScope).ConfigureAwait(false);

        return store;
    }
}
