using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;

namespace Squid.Core.Services.Publishing;

/// <summary>The queries and writes publishing needs that no project, project group, lifecycle or variable provider has.</summary>
public interface IPublishedProjectDataProvider : IScopedDependency
{
    /// <summary>
    /// Runs the work in one database transaction (or in the one already open): everything it writes is committed together, and when it throws nothing it wrote is kept
    /// and the change tracker is cleared, so the scope can be used again.
    /// </summary>
    Task RunInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct);

    /// <summary>
    /// Takes a lock, held until the current transaction ends, that only one publish of this package in this space can hold at a time: the lookup of its project and the
    /// creation of it must not interleave with another publish's. Throws when there is no transaction, because the lock would be released at once.
    /// </summary>
    Task LockPackageAsync(int spaceId, string packageId, CancellationToken ct);

    /// <summary>The project publishing created for this package (package ids matched ignoring case), or null. The oldest, so the answer never depends on the order the database returns rows in.</summary>
    Task<Persistence.Entities.Deployments.Project> FindByPackageAsync(int spaceId, string packageId, CancellationToken ct);

    Task<bool> ProjectNameExistsAsync(int spaceId, string name, CancellationToken ct);

    Task<int?> FirstProjectGroupIdAsync(int spaceId, CancellationToken ct);

    /// <summary>
    /// The space's first lifecycle (lowest id) whose first phase lists the environment: the one a new release can be deployed to it from. A lifecycle with no phases allows no environment,
    /// and one that lists the environment only in a later phase would refuse the first deployment, so neither is the answer.
    /// </summary>
    Task<int?> FirstLifecycleIdAsync(int spaceId, int environmentId, CancellationToken ct);

    /// <summary>Sets a project variable that applies only to one environment, adding it or changing its value. Returns whether anything changed.</summary>
    Task<bool> UpsertEnvironmentVariableAsync(int variableSetId, string name, int environmentId, string value, CancellationToken ct);
}

public sealed class PublishedProjectDataProvider(IRepository repository, IUnitOfWork unitOfWork) : IPublishedProjectDataProvider
{
    // The first argument of the two-key advisory lock: publishing's own, so it can never meet another feature's lock on the same hash.
    private const int AdvisoryLockNamespace = 0x50425348;

    public async Task RunInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        if (repository.Database.CurrentTransaction != null)
        {
            await work(ct).ConfigureAwait(false);
            return;
        }

        await using var transaction = await repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            await work(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            repository.ClearChangeTracker();
            throw;
        }
    }

    public async Task LockPackageAsync(int spaceId, string packageId, CancellationToken ct)
    {
        if (repository.Database.CurrentTransaction == null)
            throw new InvalidOperationException("The publish lock must be taken inside the publish transaction; outside one it would be released immediately.");

        var key = $"{spaceId}:{(packageId ?? string.Empty).Trim().ToLowerInvariant()}";

        await repository.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockNamespace}, hashtext({key}))", ct).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.Project> FindByPackageAsync(int spaceId, string packageId, CancellationToken ct)
    {
        var candidates = await repository.Query<Persistence.Entities.Deployments.Project>(p => p.SpaceId == spaceId && p.Json.Contains(PublishedProjectMarker.JsonKey)).OrderBy(p => p.Id).ToListAsync(ct).ConfigureAwait(false);

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
        var listing = from listed in repository.QueryNoTracking<LifecyclePhaseEnvironment>(e => e.EnvironmentId == environmentId)
                      join phase in repository.QueryNoTracking<LifecyclePhase>() on listed.PhaseId equals phase.Id
                      join lifecycle in repository.QueryNoTracking<Lifecycle>(l => l.SpaceId == spaceId) on phase.LifecycleId equals lifecycle.Id
                      select new { LifecycleId = lifecycle.Id, phase.SortOrder };

        var candidates = await listing.ToListAsync(ct).ConfigureAwait(false);

        if (candidates.Count == 0) return null;

        var ids = candidates.Select(c => c.LifecycleId).Distinct().ToList();
        var firstPhase = await repository.QueryNoTracking<LifecyclePhase>(p => ids.Contains(p.LifecycleId)).GroupBy(p => p.LifecycleId).Select(g => new { Id = g.Key, First = g.Min(p => p.SortOrder) }).ToListAsync(ct).ConfigureAwait(false);
        var firstOrder = firstPhase.ToDictionary(f => f.Id, f => f.First);

        return candidates.Where(c => firstOrder[c.LifecycleId] == c.SortOrder).OrderBy(c => c.LifecycleId).Select(c => (int?)c.LifecycleId).FirstOrDefault();
    }

    public async Task<bool> UpsertEnvironmentVariableAsync(int variableSetId, string name, int environmentId, string value, CancellationToken ct)
    {
        var scopeValue = environmentId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var existing = await (from variable in repository.Query<Variable>(v => v.VariableSetId == variableSetId && v.Name == name)
                              join scope in repository.QueryNoTracking<VariableScope>(s => s.ScopeType == VariableScopeType.Environment && s.ScopeValue == scopeValue) on variable.Id equals scope.VariableId
                              select variable).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (existing == null)
        {
            await AddEnvironmentVariableAsync(variableSetId, name, scopeValue, value, ct).ConfigureAwait(false);
            return true;
        }

        if (existing.Value == value) return false;

        existing.Value = value;

        await repository.UpdateAsync(existing, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    private async Task AddEnvironmentVariableAsync(int variableSetId, string name, string scopeValue, string value, CancellationToken ct)
    {
        var variable = new Variable { VariableSetId = variableSetId, Name = name, Value = value, Description = "Set by publishing the package", Type = VariableType.String };

        await repository.InsertAsync(variable, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        await repository.InsertAsync(new VariableScope { VariableId = variable.Id, ScopeType = VariableScopeType.Environment, ScopeValue = scopeValue }, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
