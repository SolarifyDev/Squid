using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;

/// <summary>Hands the acquisition a built-in package straight from its package store, verified against the hash and size recorded when it was pushed, without loading it into memory.</summary>
public sealed class BuiltInPackageFileStreamSource(IBuiltInPackageDataProvider packages, IPackageBlobStoreRegistry registry) : IPackageFileStreamSource
{
    public bool CanHandle(string feedType) => BuiltInFeed.IsBuiltIn(feedType);

    public async Task<PackageFileStream> OpenAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct)
    {
        var package = await packages.GetAsync(feed.Id, packageId, version, ct).ConfigureAwait(false) ?? throw new BuiltInPackageNotFoundException(feed.Id, packageId, version);
        var store = await registry.ResolveAsync(package.StoreId, ct).ConfigureAwait(false);
        var blob = await store.OpenReadAsync(package.BlobKey, ct).ConfigureAwait(false);

        return new PackageFileStream(new VerifyingReadStream(blob, package.PackageId, package.Version, package.SizeBytes, package.Sha256), package.SizeBytes, package.Sha256, package.FileExtension);
    }
}
