namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// Tells whether something still keeps files in a package store. Each feature that stores files implements it for its own records, and a store
/// is deleted only when none of them holds anything, so the store data provider never learns who its users are.
/// </summary>
public interface IPackageStoreUsage : IScopedDependency
{
    Task<bool> HoldsFilesAsync(int storeId, CancellationToken ct);
}
