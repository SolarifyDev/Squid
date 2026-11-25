using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution.Packages;

/// <summary>
/// Downloads and unpacks a package from one kind of external feed. <see cref="CanHandle"/> must be
/// mutually exclusive across implementations: the router fails loudly when two sources claim the
/// same feed type. Feed types no source claims are served by the router's explicit fallback.
/// </summary>
public interface IFeedPackageSource : IScopedDependency
{
    bool CanHandle(string feedType);

    Task<PackageFetchResult> FetchAsync(ExternalFeed feed, string packageId, string version, CancellationToken ct);
}
