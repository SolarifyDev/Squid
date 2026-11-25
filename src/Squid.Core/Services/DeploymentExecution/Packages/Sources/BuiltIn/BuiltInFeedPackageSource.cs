using System.Security.Cryptography;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Packages.Sources.Http;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;

/// <summary>
/// Serves a package of the built-in feed from the package store it was pushed to. Unlike the HTTP sources, which turn failures into warnings, a missing
/// version or a file the store lost or altered is an exception, so the deployment stops with the real reason. The feed type is matched exactly.
/// </summary>
public sealed class BuiltInFeedPackageSource(IBuiltInPackageDataProvider packages, IPackageBlobStoreRegistry registry) : IFeedPackageSource
{
    public bool CanHandle(string feedType) => BuiltInFeed.IsBuiltIn(feedType);

    public async Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        var package = await packages.GetAsync(feed.Id, packageId, version, ct).ConfigureAwait(false) ?? throw new BuiltInPackageNotFoundException(feed.Id, packageId, version);

        if (package.SizeBytes > Array.MaxLength)
            throw new BuiltInPackageTooLargeToDeployException(package.PackageId, package.Version, package.SizeBytes);

        var bytes = await ReadBlobAsync(package, ct).ConfigureAwait(false);

        EnsureIntact(package, bytes);

        return new PackageFetchResult(PackageArchiveExtractor.Extract(bytes), [], bytes, package.FileExtension);
    }

    private async Task<byte[]> ReadBlobAsync(BuiltInPackage package, CancellationToken ct)
    {
        var store = await registry.ResolveAsync(package.StoreId, ct).ConfigureAwait(false);

        await using var blob = await store.OpenReadAsync(package.BlobKey, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream((int)package.SizeBytes);

        await blob.CopyToAsync(buffer, ct).ConfigureAwait(false);

        return buffer.ToArray();
    }

    private static void EnsureIntact(BuiltInPackage package, byte[] bytes)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        if (actual != package.Sha256)
            throw new BuiltInPackageIntegrityException(package.PackageId, package.Version, package.Sha256, actual);
    }
}
