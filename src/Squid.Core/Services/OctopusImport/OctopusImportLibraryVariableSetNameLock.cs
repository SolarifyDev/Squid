using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportLibraryVariableSetNameLock : IScopedDependency
{
    /// <summary>
    /// Serialises library variable set creation by (space, normalised name) for the rest of the CURRENT
    /// transaction, then returns the id of a library that already holds that name (committed by another
    /// confirmation, or created earlier in this one), or <c>null</c> when the name is still free.
    /// </summary>
    Task<int?> LockNameAndFindExistingAsync(int spaceId, string name, CancellationToken ct = default);

    /// <summary>
    /// Locks every name for the rest of the CURRENT transaction in one canonical order (<see cref="OctopusImportLibraryVariableSetNameLock.LockOrder"/>),
    /// so concurrent confirmations that share names always acquire them in the same order and cannot deadlock.
    /// </summary>
    Task LockNamesAsync(int spaceId, IEnumerable<string> names, CancellationToken ct = default);
}

/// <summary>
/// library_variable_set has no unique (space_id, name) index, and adding one could fail on existing
/// databases that already hold duplicates. A Postgres transaction-scoped advisory lock gives the same
/// guarantee for imports without touching the schema: it is released automatically at commit or
/// rollback, so it is safe with pooled connections (a session-level lock would not be).
/// </summary>
public sealed class OctopusImportLibraryVariableSetNameLock(IRepository repository) : IOctopusImportLibraryVariableSetNameLock
{
    // First key of the two-key advisory lock: namespaces these locks away from any other advisory lock user.
    public const int AdvisoryLockNamespace = 0x53514C56;

    public async Task<int?> LockNameAndFindExistingAsync(int spaceId, string name, CancellationToken ct = default)
    {
        var normalizedName = Normalize(name);

        await AcquireTransactionLockAsync(spaceId, normalizedName, ct).ConfigureAwait(false);

        return await FindExistingIdAsync(spaceId, normalizedName, ct).ConfigureAwait(false);
    }

    public async Task LockNamesAsync(int spaceId, IEnumerable<string> names, CancellationToken ct = default)
    {
        foreach (var normalizedName in LockOrder(names))
            await AcquireTransactionLockAsync(spaceId, normalizedName, ct).ConfigureAwait(false);
    }

    /// <summary>Distinct normalised names in ordinal order: the one order every confirmation takes name locks in.</summary>
    public static IReadOnlyList<string> LockOrder(IEnumerable<string> names)
        => (names ?? []).Select(Normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    // Same identity rule as conflict discovery (trimmed, case-insensitive), so "Shared" and " shared " contend.
    public static string Normalize(string name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    private async Task AcquireTransactionLockAsync(int spaceId, string normalizedName, CancellationToken ct)
    {
        if (repository.Database.CurrentTransaction == null)
            throw new InvalidOperationException("The library variable set name lock must be taken inside the import transaction; outside one it would be released immediately.");

        var key = $"{spaceId}:{normalizedName}";

        await repository.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockNamespace}, hashtext({key}))", ct).ConfigureAwait(false);
    }

    private async Task<int?> FindExistingIdAsync(int spaceId, string normalizedName, CancellationToken ct)
    {
        var ids = await repository.QueryNoTracking<LibraryVariableSet>(l => l.SpaceId == spaceId && l.Name.Trim().ToLower() == normalizedName).OrderBy(l => l.Id).Select(l => l.Id).Take(1).ToListAsync(ct).ConfigureAwait(false);

        return ids.Count == 0 ? null : ids[0];
    }
}
