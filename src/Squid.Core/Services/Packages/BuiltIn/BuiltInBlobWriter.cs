using System.Security.Cryptography;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Constants;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Where a pushed file was kept: the package store and the key in it.</summary>
internal sealed record StoredBlob(int StoreId, string Key);

/// <summary>Keeps a staged upload in the feed's package store, by content.</summary>
internal interface IBuiltInBlobWriter : IScopedDependency
{
    Task<StoredBlob> WriteAsync(ExternalFeed feed, StagedUpload staged, BuiltInPackageFormat format, CancellationToken ct);
}

internal sealed class BuiltInBlobWriter(IPackageStoreDataProvider stores, IPackageBlobStoreRegistry registry) : IBuiltInBlobWriter
{
    /// <summary>A blob up to this size that already exists is also compared by hash before a write is skipped; a bigger one by size only, as reading it all back would cost as much as writing it.</summary>
    private const long HashCheckLimitBytes = 16L * 1024 * 1024;

    public async Task<StoredBlob> WriteAsync(ExternalFeed feed, StagedUpload staged, BuiltInPackageFormat format, CancellationToken ct)
    {
        var storeId = await ReadStoreIdAsync(feed, ct).ConfigureAwait(false);
        var store = await registry.ResolveAsync(storeId, ct).ConfigureAwait(false);
        var key = BuiltInBlobKey.For(staged.Sha256, BuiltInPackageFormats.ExtensionOf(format));

        // Content addressed: when the store already holds intact bytes under this key (another version or package pushed the same file) nothing is written. A blob that
        // is there but not intact (truncated or corrupt) is written again, so pushing the file once more repairs it.
        // Blobs are written before their index row, and nothing here removes a blob a failed push leaves behind (or a replaced version's old one): the reconcile job that
        // sweeps blobs no row points at is a later change.
        if (await ExistsIntactAsync(store, key, staged, ct).ConfigureAwait(false)) return new StoredBlob(storeId, key);

        await using var file = File.OpenRead(staged.Path);

        await store.PutAsync(key, file, ct).ConfigureAwait(false);

        return new StoredBlob(storeId, key);
    }

    // The feed's StoreId property picks the store; without one the default store is used, and its id is recorded either way so the package stays findable
    // after the default moves.
    private async Task<int> ReadStoreIdAsync(ExternalFeed feed, CancellationToken ct)
    {
        var configured = ExternalFeedProperties.GetString(feed, BuiltInFeed.StoreIdProperty);

        if (string.IsNullOrWhiteSpace(configured))
            return (await stores.GetDefaultAsync(ct).ConfigureAwait(false))?.Id ?? throw new NoDefaultPackageStoreException();

        return int.TryParse(configured, out var storeId) ? storeId : throw new BuiltInFeedStoreInvalidException(feed.Id, configured);
    }

    private static async Task<bool> ExistsIntactAsync(IPackageBlobStore store, string key, StagedUpload staged, CancellationToken ct)
    {
        if (!await store.ExistsAsync(key, ct).ConfigureAwait(false)) return false;

        try
        {
            await using var stored = await store.OpenReadAsync(key, ct).ConfigureAwait(false);

            return await MatchesAsync(stored, staged, ct).ConfigureAwait(false);
        }
        catch (PackageBlobNotFoundException)
        {
            return false;
        }
    }

    private static async Task<bool> MatchesAsync(Stream stored, StagedUpload staged, CancellationToken ct)
    {
        if (stored.CanSeek && stored.Length != staged.SizeBytes) return false;

        if (stored.CanSeek && staged.SizeBytes > HashCheckLimitBytes) return true;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await stored.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;

            if (total > staged.SizeBytes) return false;

            hash.AppendData(buffer, 0, read);
        }

        return total == staged.SizeBytes && (staged.SizeBytes > HashCheckLimitBytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() == staged.Sha256);
    }
}
