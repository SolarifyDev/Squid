using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Packages;

/// <summary>
/// A sibling of <see cref="IFeedPackageSource"/> for a feed whose packages can be handed over as a stream instead of a byte array: the acquisition copies the stream
/// straight to its file, so a big package is never held in memory. A feed type served by both is acquired through this one; every other feed type goes through
/// <see cref="IPackageContentFetcher"/> exactly as before.
/// </summary>
public interface IPackageFileStreamSource : IScopedDependency
{
    bool CanHandle(string feedType);

    /// <summary>Opens the package file. The caller disposes the result. Throws when the package or its file is missing.</summary>
    Task<PackageFileStream> OpenAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct);
}

/// <summary>An opened package file: the content to read once from the start, its size and SHA-256 (lower-case hex) as recorded, and the real file extension.</summary>
/// <param name="Content">Reading it to the end throws when the bytes read are not those of <paramref name="Sha256"/> and <paramref name="SizeBytes"/>.</param>
public sealed record PackageFileStream(Stream Content, long SizeBytes, string Sha256, string FileExtension) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
