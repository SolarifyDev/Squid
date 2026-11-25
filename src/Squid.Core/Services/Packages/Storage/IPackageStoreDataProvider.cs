using Squid.Core.Persistence.Entities.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// The only reader and writer of the package_store table. The Secret column is protected at rest here: entities it returns carry the
/// decrypted secret JSON and are never tracked, so nothing decrypted can be saved back by a later SaveChanges.
/// </summary>
public interface IPackageStoreDataProvider : IScopedDependency
{
    Task<List<PackageStore>> GetAllAsync(CancellationToken ct);

    Task<PackageStore> GetByIdAsync(int id, CancellationToken ct);

    Task<PackageStore> GetDefaultAsync(CancellationToken ct);

    /// <summary>Whether another store (not <paramref name="exceptId"/>) already has the name, ignoring case.</summary>
    Task<bool> NameExistsAsync(string name, int? exceptId, CancellationToken ct);

    /// <summary>Protects <see cref="PackageStore.Secret"/> and inserts the store; the entity's Id is set on return. Throws <c>PackageStoreNameConflictException</c> when the database finds the name taken.</summary>
    Task AddAsync(PackageStore store, CancellationToken ct);

    /// <summary>
    /// Saves the name, properties and secret of a store read with this provider, and nothing else: whether the store is the default and its last test
    /// result are whatever the database holds now, so an edit can never undo a concurrent set-default or test. <paramref name="resetTestResult"/> clears
    /// the last test result (the settings it described are gone). Throws <c>PackageStoreNotFoundException</c> when the store was deleted meanwhile.
    /// </summary>
    Task UpdateAsync(PackageStore store, bool resetTestResult, CancellationToken ct);

    /// <summary>Deletes the store unless it is (or has just become) the default. Returns whether a row was deleted.</summary>
    Task<bool> DeleteIfNotDefaultAsync(int id, CancellationToken ct);

    /// <summary>Makes the store the only default in one transaction. Returns false (changing nothing) when there is no such store; throws <c>PackageStoreConcurrentChangeException</c> when it loses a race.</summary>
    Task<bool> SetDefaultAsync(int id, CancellationToken ct);

    /// <summary>Records the outcome of a test without touching the settings or the modification date.</summary>
    Task RecordTestResultAsync(int id, bool success, string message, CancellationToken ct);
}
