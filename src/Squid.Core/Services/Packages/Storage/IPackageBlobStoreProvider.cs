using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>One kind of package store (local disk, and later others): describes its settings and builds a store from saved ones.</summary>
public interface IPackageBlobStoreProvider
{
    /// <summary>The value saved in a store record to say which provider owns it. Saved records refer to it: never change it.</summary>
    string Kind { get; }

    PackageStoreProviderDescriptor Describe();

    IPackageBlobStore Create(PackageBlobStoreConfiguration configuration);

    /// <summary>Writes, reads back and deletes a probe key under <see cref="PackageBlobStoreProbe.KeyPrefix"/>; reports the outcome in words. Never throws for a store that does not work.</summary>
    Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct);
}

/// <summary>Implemented by a provider that can tell the operator something about saved settings without touching the store.</summary>
public interface IPackageBlobStoreWarnings
{
    IReadOnlyList<string> GetWarnings(PackageBlobStoreConfiguration configuration);
}
