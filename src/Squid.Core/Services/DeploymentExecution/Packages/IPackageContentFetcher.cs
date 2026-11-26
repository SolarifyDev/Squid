using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Packages;

public interface IPackageContentFetcher : IScopedDependency
{
    Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct);
}

/// <param name="FileExtension">The extension the archive really has (for example <c>.tar.gz</c>), for a source that knows it. Null lets the acquisition guess from the feed and package id.</param>
public record PackageFetchResult(Dictionary<string, byte[]> Files, List<string> Warnings, byte[] RawBytes, string FileExtension = null);
