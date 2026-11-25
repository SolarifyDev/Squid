using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Publishing;

/// <summary>The queries publishing needs that no project, project group or lifecycle provider has: finding the project a package already runs as, and the space's defaults.</summary>
public interface IPublishedProjectDataProvider : IScopedDependency
{
    /// <summary>The project publishing created for this package (package ids matched ignoring case), or null.</summary>
    Task<Persistence.Entities.Deployments.Project> FindByPackageAsync(int spaceId, string packageId, CancellationToken ct);

    Task<bool> ProjectNameExistsAsync(int spaceId, string name, CancellationToken ct);

    Task<int?> FirstProjectGroupIdAsync(int spaceId, CancellationToken ct);

    /// <summary>The space's first lifecycle (lowest id) that lists the environment in one of its phases. A lifecycle with no phases allows no environment, so it is never the answer.</summary>
    Task<int?> FirstLifecycleIdAsync(int spaceId, int environmentId, CancellationToken ct);
}

public sealed class PublishedProjectDataProvider(IRepository repository) : IPublishedProjectDataProvider
{
    public async Task<Persistence.Entities.Deployments.Project> FindByPackageAsync(int spaceId, string packageId, CancellationToken ct)
    {
        var candidates = await repository.Query<Persistence.Entities.Deployments.Project>(p => p.SpaceId == spaceId && p.Json.Contains(PublishedProjectMarker.JsonKey)).ToListAsync(ct).ConfigureAwait(false);

        return candidates.FirstOrDefault(p => string.Equals(PublishedProjectMarker.TryParse(p.Json)?.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> ProjectNameExistsAsync(int spaceId, string name, CancellationToken ct)
    {
        return await repository.QueryNoTracking<Persistence.Entities.Deployments.Project>(p => p.SpaceId == spaceId && p.Name == name).AnyAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> FirstProjectGroupIdAsync(int spaceId, CancellationToken ct)
    {
        return await repository.QueryNoTracking<ProjectGroup>(g => g.SpaceId == spaceId).OrderBy(g => g.Id).Select(g => (int?)g.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> FirstLifecycleIdAsync(int spaceId, int environmentId, CancellationToken ct)
    {
        var lifecycleIds = from listed in repository.QueryNoTracking<LifecyclePhaseEnvironment>(e => e.EnvironmentId == environmentId)
                           join phase in repository.QueryNoTracking<LifecyclePhase>() on listed.PhaseId equals phase.Id
                           join lifecycle in repository.QueryNoTracking<Lifecycle>(l => l.SpaceId == spaceId) on phase.LifecycleId equals lifecycle.Id
                           orderby lifecycle.Id
                           select (int?)lifecycle.Id;

        return await lifecycleIds.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }
}
