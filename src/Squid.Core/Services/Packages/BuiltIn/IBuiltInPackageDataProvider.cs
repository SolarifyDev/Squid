using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.BuiltInPackages;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>The only reader and writer of the built-in package index. Package ids are matched ignoring case everywhere here; versions are matched exactly.</summary>
public interface IBuiltInPackageDataProvider : IScopedDependency
{
    /// <summary>
    /// The casing of the package id the feed keeps: the first one ever pushed. Fixes <paramref name="packageId"/>'s casing as the canonical one when the feed has
    /// no such package yet; atomic, so two first pushes racing with different casings still agree on one.
    /// </summary>
    Task<string> ResolveCanonicalIdAsync(int feedId, string packageId, CancellationToken ct);

    Task<BuiltInPackage> GetAsync(int feedId, string packageId, string version, CancellationToken ct);

    Task<List<BuiltInPackage>> ListVersionsAsync(int feedId, string packageId, CancellationToken ct);

    /// <summary>The packages whose id contains <paramref name="filter"/> (all when empty), ordered by id.</summary>
    Task<List<BuiltInPackageDto>> ListPackagesAsync(int feedId, string filter, int take, CancellationToken ct);

    /// <summary>Adds the version. Throws <c>PackageVersionAlreadyExistsException</c> when it exists, even when another push added it a moment ago.</summary>
    Task InsertAsync(BuiltInPackage package, CancellationToken ct);

    /// <summary>Overwrites what the stored version points at (its file and who published it when and how), keeping its id, package id and version.</summary>
    Task ReplaceContentAsync(int id, BuiltInPackage content, CancellationToken ct);
}
