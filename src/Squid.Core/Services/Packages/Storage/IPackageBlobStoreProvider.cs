using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>One kind of package store (local disk, and later others): describes its settings and builds a store from saved ones.</summary>
public interface IPackageBlobStoreProvider
{
    /// <summary>The value saved in a store record to say which provider owns it. Saved records refer to it: never change it.</summary>
    string Kind { get; }

    PackageStoreProviderDescriptor Describe();

    /// <summary>
    /// What is wrong with the settings beyond what the descriptor can say (a folder that cannot be resolved, a system folder); empty when they are usable.
    /// Run before a store is saved or tested, so a row that would break listing or resolving is never written. Must not throw and must not touch the store.
    /// </summary>
    IReadOnlyList<string> ValidateConfiguration(PackageBlobStoreConfiguration configuration);

    IPackageBlobStore Create(PackageBlobStoreConfiguration configuration);

    /// <summary>Writes, reads back and deletes a probe key under <see cref="PackageBlobStoreProbe.KeyPrefix"/>; reports the outcome in words. Never throws for a store that does not work.</summary>
    Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct);
}

/// <summary>Implemented by a provider that can say where saved settings keep the data, without touching the store. Must not throw; null when unknown.</summary>
public interface IPackageBlobStoreLocation
{
    string DescribeLocation(PackageBlobStoreConfiguration configuration);
}

/// <summary>Implemented by a provider that can tell the operator something about saved settings without touching the store.</summary>
public interface IPackageBlobStoreWarnings
{
    IReadOnlyList<string> GetWarnings(PackageBlobStoreConfiguration configuration);
}
