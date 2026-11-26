namespace Squid.Core.Services.Packages.Storage;

/// <summary>Turns a saved store record into a working <see cref="IPackageBlobStore"/>.</summary>
public interface IPackageBlobStoreRegistry
{
    /// <summary>Throws <c>PackageStoreNotFoundException</c> for an unknown id and <c>PackageStoreProviderNotFoundException</c> for a kind no provider implements.</summary>
    Task<IPackageBlobStore> ResolveAsync(int storeId, CancellationToken ct);

    /// <summary>Throws <c>NoDefaultPackageStoreException</c> when no store is the default.</summary>
    Task<IPackageBlobStore> ResolveDefaultAsync(CancellationToken ct);
}
