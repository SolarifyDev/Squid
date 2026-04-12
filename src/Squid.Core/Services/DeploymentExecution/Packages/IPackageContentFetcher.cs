using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Packages;

public interface IPackageContentFetcher : IScopedDependency
{
    Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct);
}

public record PackageFetchResult(Dictionary<string, byte[]> Files, List<string> Warnings, byte[] RawBytes);
