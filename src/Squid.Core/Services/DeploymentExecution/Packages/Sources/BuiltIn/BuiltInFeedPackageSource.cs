using System.Security.Cryptography;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Packages.Sources.BuiltIn;

/// <summary>
/// Serves a package of the built-in feed as a <see cref="PackageFetchResult"/>, for the consumers that read its <c>Files</c> (a Kubernetes YAML step). The deployment
/// path that only needs the archive file does not come through here: it uses <see cref="BuiltInPackageFileStreamSource"/> and never holds the package in memory. The
/// package is read once into an array of exactly its size, its files are read from that array without buffering the rest, and unlike the HTTP sources, which turn
/// failures into warnings, a missing version or a file the store lost or altered is an exception. The feed type is matched exactly.
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
        var warnings = new List<string>();

        return new PackageFetchResult(BuiltInArchiveFileExtractor.Extract(bytes, warnings), warnings, bytes, package.FileExtension);
    }

    private async Task<byte[]> ReadBlobAsync(BuiltInPackage package, CancellationToken ct)
    {
        var store = await registry.ResolveAsync(package.StoreId, ct).ConfigureAwait(false);

        await using var blob = new VerifyingReadStream(await store.OpenReadAsync(package.BlobKey, ct).ConfigureAwait(false), package.PackageId, package.Version, package.SizeBytes, package.Sha256);

        var bytes = new byte[package.SizeBytes];
        var filled = 0;

        while (filled < bytes.Length)
        {
            var read = await blob.ReadAsync(bytes.AsMemory(filled), ct).ConfigureAwait(false);

            if (read == 0) break;

            filled += read;
        }

        // Reading on past the recorded size to the end makes the stream compare what it saw with what was recorded: a short or altered file fails here.
        if (filled < bytes.Length || await blob.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0)
            throw new BuiltInPackageIntegrityException(package.PackageId, package.Version, package.Sha256, "a file of another size");

        return bytes;
    }
}
