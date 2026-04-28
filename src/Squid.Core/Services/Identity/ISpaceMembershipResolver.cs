using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Services.Identity;

/// <summary>
/// P0-Phase10.3 (audit D.3 / H-19) — resolves whether a user has membership
/// in a given Space via the Team → TeamMember chain.
///
/// <para><b>Why this exists</b>: pre-Phase-10.3, the only thing gating cross-
/// space access was that controllers checked authorization-via-permission
/// AGAINST the SpaceId already injected from the X-Space-Id HTTP header.
/// A user in Space-1 could send <c>X-Space-Id: 2</c>, the
/// <see cref="Squid.Core.Middlewares.SpaceScope.SpaceIdInjectionSpecification"/>
/// trusted the header, and the user could read/mutate Space-2 resources
/// despite having NO team membership in Space-2.</para>
///
/// <para>This resolver is consumed by a new gate middleware that runs
/// BEFORE the injection — if the requesting user isn't a member of the
/// requested space, the request is rejected with 403 before any handler
/// sees a Space-2 SpaceId.</para>
/// </summary>
public interface ISpaceMembershipResolver : IScopedDependency
{
    /// <summary>
    /// True iff <paramref name="userId"/> has at least one
    /// <see cref="TeamMember"/> row whose <see cref="Team"/> belongs to
    /// <paramref name="spaceId"/>.
    /// </summary>
    Task<bool> IsMemberAsync(int userId, int spaceId, CancellationToken ct = default);
}

public sealed class SpaceMembershipResolver : ISpaceMembershipResolver
{
    private readonly IRepository _repository;

    public SpaceMembershipResolver(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<bool> IsMemberAsync(int userId, int spaceId, CancellationToken ct = default)
    {
        // Two-step async query (intentionally simple — operator-readable in profiler):
        //   1. team IDs the user is a member of
        //   2. ANY of those teams in the requested space
        // The single-query variant via join works too but the materialised
        // intermediate set keeps the predicate filter simple and matches
        // the existing GetByTeamIdsAsync pattern in ScopedUserRoleDataProvider.
        //
        // CRITICAL: must use ToListAsync / AnyAsync — sync ToList()/Any() on EF
        // blocks the Mediator pipeline thread, which on a hot HTTP path can
        // exhaust the threadpool under bursty load. Same async-discipline as
        // every other resolver in Squid.Core.
        var userTeamIds = await _repository.Query<TeamMember>(tm => tm.UserId == userId)
            .Select(tm => tm.TeamId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (userTeamIds.Count == 0) return false;

        var hasMatch = await _repository.Query<Team>(t => t.SpaceId == spaceId)
            .AnyAsync(t => userTeamIds.Contains(t.Id), ct)
            .ConfigureAwait(false);

        return hasMatch;
    }
}
