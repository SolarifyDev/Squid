using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// Protects <see cref="PackageStore.Secret"/> the same way <c>ExternalFeedDataProvider</c> protects a feed password: the V2 envelope on every write,
/// read-both on every read. Every read here is no-tracking, so decrypting the entity in memory can never be flushed back as plaintext by a later
/// SaveChanges in the same scope.
/// </summary>
public class PackageStoreDataProvider(IUnitOfWork unitOfWork, IRepository repository, IAtRestSecretProtector protector) : IPackageStoreDataProvider
{
    // The V2 envelope derives its key from a random per-payload salt, so the legacy KDF-scope argument is irrelevant here.
    private const int SecretKdfScope = 0;

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
        return repository.QueryNoTracking<PackageStore>(s => s.Name == name && s.Id != exceptId).AnyAsync(ct);
    }

    public async Task AddAsync(PackageStore store, CancellationToken ct)
    {
        store.Secret = protector.Protect(store.Secret, SecretKdfScope);

        await repository.InsertAsync(store, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(PackageStore store, CancellationToken ct)
    {
        store.Secret = protector.Protect(store.Secret, SecretKdfScope);

        await repository.UpdateAsync(store, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteIfNotDefaultAsync(int id, CancellationToken ct)
    {
        var deleted = await repository.ExecuteDeleteAsync<PackageStore>(s => s.Id == id && !s.IsDefault, ct).ConfigureAwait(false);

        return deleted > 0;
    }

    public async Task<bool> SetDefaultAsync(int id, CancellationToken ct)
    {
        await using var transaction = await repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var target = await repository.Query<PackageStore>(s => s.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (target == null) return false;

        await ClearDefaultAsync(id, ct).ConfigureAwait(false);

        target.IsDefault = true;

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return true;
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
    private async Task ClearDefaultAsync(int exceptId, CancellationToken ct)
    {
        var current = await repository.Query<PackageStore>(s => s.IsDefault && s.Id != exceptId).ToListAsync(ct).ConfigureAwait(false);

        foreach (var store in current)
            store.IsDefault = false;

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // Read-both: an unprefixed value is returned verbatim by Unprotect.
    private async Task<PackageStore> DecryptSecretAsync(PackageStore store)
    {
        if (store == null || string.IsNullOrEmpty(store.Secret)) return store;

        store.Secret = await protector.UnprotectAsync(store.Secret, SecretKdfScope).ConfigureAwait(false);

        return store;
    }
}
