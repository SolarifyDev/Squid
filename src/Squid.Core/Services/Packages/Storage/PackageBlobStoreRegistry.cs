using System.Collections.Concurrent;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Packages.Storage.Exceptions;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// Looks the store record up on every call (a one-row read) and keeps the store it built from it, keyed by id and the record's LastModifiedDate:
/// an edited record has a newer date, so it yields a fresh store at once, on every replica, with no invalidation to forget. A singleton, so each
/// lookup reads through a short-lived child scope.
/// </summary>
public sealed class PackageBlobStoreRegistry(ILifetimeScope scope, IEnumerable<IPackageBlobStoreProvider> providers) : IPackageBlobStoreRegistry, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, CachedStore> _stores = new();

    public async Task<IPackageBlobStore> ResolveAsync(int storeId, CancellationToken ct)
    {
        var record = await ReadAsync(provider => provider.GetByIdAsync(storeId, ct)).ConfigureAwait(false);

        if (record == null)
            throw new PackageStoreNotFoundException(storeId);

        return GetOrCreate(record);
    }

    public async Task<IPackageBlobStore> ResolveDefaultAsync(CancellationToken ct)
    {
        var record = await ReadAsync(provider => provider.GetDefaultAsync(ct)).ConfigureAwait(false);

        if (record == null)
            throw new NoDefaultPackageStoreException();

        return GetOrCreate(record);
    }

    private async Task<PackageStore> ReadAsync(Func<IPackageStoreDataProvider, Task<PackageStore>> read)
    {
        await using var child = scope.BeginLifetimeScope();

        return await read(child.Resolve<IPackageStoreDataProvider>()).ConfigureAwait(false);
    }

    private IPackageBlobStore GetOrCreate(PackageStore record)
    {
        var cached = _stores.GetValueOrDefault(record.Id);

        if (cached != null && cached.LastModifiedDate == record.LastModifiedDate) return cached.Store;

        var fresh = new CachedStore(record.LastModifiedDate, CreateStore(record));

        _stores[record.Id] = fresh;

        return fresh.Store;
    }

    private IPackageBlobStore CreateStore(PackageStore record)
    {
        var provider = providers.FirstOrDefault(p => p.Kind == record.Kind) ?? throw new PackageStoreProviderNotFoundException(record.Kind);

        return provider.Create(PackageStoreConfigurationMapper.ToConfiguration(record));
    }

    private sealed record CachedStore(DateTimeOffset LastModifiedDate, IPackageBlobStore Store);
}
