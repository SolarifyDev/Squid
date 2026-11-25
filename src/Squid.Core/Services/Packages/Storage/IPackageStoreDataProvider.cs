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

    Task<bool> NameExistsAsync(string name, int? exceptId, CancellationToken ct);

    /// <summary>Protects <see cref="PackageStore.Secret"/> and inserts the store; the entity's Id is set on return.</summary>
    Task AddAsync(PackageStore store, CancellationToken ct);

    /// <summary>Protects <see cref="PackageStore.Secret"/> and saves every field of a store read with this provider.</summary>
    Task UpdateAsync(PackageStore store, CancellationToken ct);

    /// <summary>Deletes the store unless it is (or has just become) the default. Returns whether a row was deleted.</summary>
    Task<bool> DeleteIfNotDefaultAsync(int id, CancellationToken ct);

    /// <summary>Makes the store the only default in one transaction. Returns false (changing nothing) when there is no such store.</summary>
    Task<bool> SetDefaultAsync(int id, CancellationToken ct);

    /// <summary>Records the outcome of a test without touching the settings or the modification date.</summary>
    Task RecordTestResultAsync(int id, bool success, string message, CancellationToken ct);
}
