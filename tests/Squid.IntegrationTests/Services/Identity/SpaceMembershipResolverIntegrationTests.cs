using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;

namespace Squid.IntegrationTests.Services.Identity;

/// <summary>
/// End-to-end exercise of <see cref="ISpaceMembershipResolver"/> against the real
/// EF-backed data layer. Unit tests in
/// <c>Squid.UnitTests.Services.Identity.SpaceMembershipResolverTests</c> cover the
/// decision matrix with mocked queryables; integration coverage matters here for
/// two reasons that mock tests cannot reach:
///
/// <list type="bullet">
///   <item><b>LINQ → SQL translation</b>: the <c>sur.SpaceId == null</c> clause
///         must translate to <c>IS NULL</c> (NOT <c>= NULL</c>, which Postgres
///         silently evaluates as <c>UNKNOWN</c> → never matches). EF Core does
///         translate this correctly, but pinning it against a real Postgres
///         catches a future provider/version regression.</item>
///   <item><b>DI graph wiring</b>: the resolver depends on <c>IRepository</c>
///         registered against the real DbContext. A composition-time mis-wire
///         (e.g., a new transient repository scoped wrong) would be invisible
///         in unit tests.</item>
/// </list>
///
/// <para>The Path-2 indirect-membership case (global Team via ScopedUserRole)
/// is the seeded "Squid Administrators" → default Space scenario that
/// motivated the resolver fix; it is the regression these tests guard.</para>
/// </summary>
public class SpaceMembershipResolverIntegrationTests : TestBase
{
    public SpaceMembershipResolverIntegrationTests()
        : base("SpaceMembershipResolver", "squid_it_space_membership_resolver")
    {
    }

    [Fact]
    public async Task IsMemberAsync_DirectMembership_ReturnsTrue()
    {
        // Path 1: Team has SpaceId = 5, user is a TeamMember of that team.
        var (userId, _, _) = await SeedDirectMembershipAsync(teamSpaceId: 5).ConfigureAwait(false);

        await Run<ISpaceMembershipResolver>(async resolver =>
        {
            (await resolver.IsMemberAsync(userId, spaceId: 5).ConfigureAwait(false)).ShouldBeTrue(
                customMessage: "Direct membership (Team.SpaceId == requestedSpaceId) must succeed against real EF.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task IsMemberAsync_DirectMembership_DifferentSpace_ReturnsFalse()
    {
        // Privesc vector pin against real EF: a user in Space 5 must NOT be
        // reported as a member of Space 99 — same shape as the resolver unit
        // test but verified end-to-end through the real query translation.
        var (userId, _, _) = await SeedDirectMembershipAsync(teamSpaceId: 5).ConfigureAwait(false);

        await Run<ISpaceMembershipResolver>(async resolver =>
        {
            (await resolver.IsMemberAsync(userId, spaceId: 99).ConfigureAwait(false)).ShouldBeFalse(
                customMessage: "User in Space 5 must NOT be admitted to Space 99 — privesc vector pin against real EF.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task IsMemberAsync_IndirectViaScopedRoleInRequestedSpace_ReturnsTrue()
    {
        // Path 2 (the seeded admin scenario): User is a member of a GLOBAL team
        // (Team.SpaceId = 0) which has a ScopedUserRole binding it to Space 5
        // via SpaceOwner. The resolver must admit the user to Space 5 even
        // though no Team they belong to has SpaceId = 5.
        const int targetSpaceId = 5;
        var (userId, _, _) = await SeedIndirectMembershipAsync(
            teamSpaceId: 0,
            grantSpaceId: targetSpaceId).ConfigureAwait(false);

        await Run<ISpaceMembershipResolver>(async resolver =>
        {
            (await resolver.IsMemberAsync(userId, targetSpaceId).ConfigureAwait(false)).ShouldBeTrue(
                customMessage: "Global team (SpaceId=0) with ScopedUserRole(SpaceId=5) must admit user to Space 5. " +
                               "This is the seeded admin path — the regression this fix targets.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task IsMemberAsync_IndirectViaSystemWideScopedRole_ReturnsTrueForAnySpace()
    {
        // Path 2 + ScopedUserRole.SpaceId IS NULL: system-wide grant. The user
        // must be reported as a member of EVERY space, regardless of which
        // SpaceId the gate asks about. This is the SystemAdministrator
        // assignment shape. CRITICAL: pins the EF translation of `== null` to
        // `IS NULL` in the WHERE clause — `= NULL` would silently always be
        // false in Postgres and the system admin would lose access.
        var (userId, _, _) = await SeedIndirectMembershipAsync(
            teamSpaceId: 0,
            grantSpaceId: null).ConfigureAwait(false);

        await Run<ISpaceMembershipResolver>(async resolver =>
        {
            (await resolver.IsMemberAsync(userId, spaceId: 42).ConfigureAwait(false)).ShouldBeTrue(
                customMessage: "ScopedUserRole(SpaceId=null) is a system-wide grant — must admit to ANY space. " +
                               "EF must translate `== null` to `IS NULL`, not `= NULL`. If this fails, system " +
                               "admins lose access to every Space silently — the EF translation pin is the whole point.");

            (await resolver.IsMemberAsync(userId, spaceId: 7).ConfigureAwait(false)).ShouldBeTrue(
                customMessage: "Same as above for a different SpaceId — system-wide grants are space-agnostic.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task IsMemberAsync_IndirectViaScopedRoleInDifferentSpace_ReturnsFalse()
    {
        // Privesc protection for Path 2: user has ScopedUserRole binding their
        // global team to Space 5, but querying Space 99 must STILL return false.
        // Path 2 must not over-grant — the SQL filter has to discriminate by
        // requested SpaceId. Mirrors the unit test pin against real EF.
        var (userId, _, _) = await SeedIndirectMembershipAsync(
            teamSpaceId: 0,
            grantSpaceId: 5).ConfigureAwait(false);

        await Run<ISpaceMembershipResolver>(async resolver =>
        {
            (await resolver.IsMemberAsync(userId, spaceId: 99).ConfigureAwait(false)).ShouldBeFalse(
                customMessage: "User with ScopedUserRole(SpaceId=5) must NOT be admitted to Space 99. " +
                               "Path 2 must filter by requested space, not over-grant.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task IsMemberAsync_NoTeamMembership_ReturnsFalse()
    {
        // Defensive: a user with NO TeamMember rows must short-circuit to false
        // before either path runs. Pins the empty-userTeamIds early-return path.
        var userId = await SeedUserOnlyAsync().ConfigureAwait(false);

        await Run<ISpaceMembershipResolver>(async resolver =>
        {
            (await resolver.IsMemberAsync(userId, spaceId: 1).ConfigureAwait(false)).ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    // ── Seeding helpers ──────────────────────────────────────────────────────
    //
    // Each helper inserts a fresh user (auto-incremented Id) so tests don't
    // collide on UserId values. Returns the materialised IDs for assertion.

    private async Task<(int userId, int teamId, int? scopedRoleId)> SeedDirectMembershipAsync(int teamSpaceId)
    {
        var userId = 0;
        var teamId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var userName = $"u_{Guid.NewGuid():N}";
            var user = new UserAccount
            {
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                DisplayName = userName,
                PasswordHash = "x",
                IsDisabled = false,
                IsSystem = false
            };
            await repo.InsertAsync(user).ConfigureAwait(false);

            var team = new Team { Name = $"t_{Guid.NewGuid():N}", SpaceId = teamSpaceId };
            await repo.InsertAsync(team).ConfigureAwait(false);

            await uow.SaveChangesAsync().ConfigureAwait(false);

            await repo.InsertAsync(new TeamMember { UserId = user.Id, TeamId = team.Id }).ConfigureAwait(false);
            await uow.SaveChangesAsync().ConfigureAwait(false);

            userId = user.Id;
            teamId = team.Id;
        }).ConfigureAwait(false);

        return (userId, teamId, null);
    }

    private async Task<(int userId, int teamId, int scopedRoleId)> SeedIndirectMembershipAsync(int teamSpaceId, int? grantSpaceId)
    {
        var userId = 0;
        var teamId = 0;
        var scopedRoleId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var userName = $"u_{Guid.NewGuid():N}";
            var user = new UserAccount
            {
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                DisplayName = userName,
                PasswordHash = "x",
                IsDisabled = false,
                IsSystem = false
            };
            await repo.InsertAsync(user).ConfigureAwait(false);

            var team = new Team { Name = $"t_{Guid.NewGuid():N}", SpaceId = teamSpaceId };
            await repo.InsertAsync(team).ConfigureAwait(false);

            var role = new UserRole { Name = $"r_{Guid.NewGuid():N}", IsBuiltIn = false };
            await repo.InsertAsync(role).ConfigureAwait(false);

            await uow.SaveChangesAsync().ConfigureAwait(false);

            await repo.InsertAsync(new TeamMember { UserId = user.Id, TeamId = team.Id }).ConfigureAwait(false);

            var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = grantSpaceId };
            await repo.InsertAsync(scopedRole).ConfigureAwait(false);

            await uow.SaveChangesAsync().ConfigureAwait(false);

            userId = user.Id;
            teamId = team.Id;
            scopedRoleId = scopedRole.Id;
        }).ConfigureAwait(false);

        return (userId, teamId, scopedRoleId);
    }

    private async Task<int> SeedUserOnlyAsync()
    {
        var userId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var userName = $"u_{Guid.NewGuid():N}";
            var user = new UserAccount
            {
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                DisplayName = userName,
                PasswordHash = "x",
                IsDisabled = false,
                IsSystem = false
            };
            await repo.InsertAsync(user).ConfigureAwait(false);
            await uow.SaveChangesAsync().ConfigureAwait(false);

            userId = user.Id;
        }).ConfigureAwait(false);

        return userId;
    }
}
