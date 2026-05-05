using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Identity;

namespace Squid.UnitTests.Services.Identity;

/// <summary>
/// P0-Phase10.3 (audit D.3 / H-19) — pin the user-space membership lookup.
///
/// <para><b>The privesc vector this protects against</b>: a user with team
/// membership in Space-1 sends a command to a controller, with HTTP header
/// <c>X-Space-Id: 2</c>. , the SpaceIdInjectionSpecification
/// trusted the header and injected SpaceId=2 into the command, allowing
/// the user to read/mutate Space-2's resources despite having no membership
/// there. This was the only TRUE cross-space privilege-escalation vector
/// in the audit's findings.</para>
///
/// <para>Resolver contract: <see cref="ISpaceMembershipResolver.IsMemberAsync"/>
/// returns true iff the user has at least one TeamMember row whose Team is
/// in the requested space. The middleware (tested separately) calls this
/// before allowing the header SpaceId to be injected.</para>
/// </summary>
public sealed class SpaceMembershipResolverTests
{
    private readonly Mock<IRepository> _repository = new();

    [Fact]
    public async Task IsMemberAsync_UserInTeamInSpace_ReturnsTrue()
    {
        // User 1 is in Team 10, Team 10 is in Space 5 → user IS a member of space 5
        var teamMembers = new[] { new TeamMember { TeamId = 10, UserId = 1 } }.AsQueryable();
        var teams = new[] { new Team { Id = 10, SpaceId = 5 } }.AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 1, spaceId: 5)).ShouldBeTrue();
    }

    [Fact]
    public async Task IsMemberAsync_UserInTeamInDifferentSpace_ReturnsFalse()
    {
        // User 1 is in Team 10, Team 10 is in Space 5 — but they're querying for space 99
        // → membership check must say NO (this is exactly the privesc vector)
        var teamMembers = new[] { new TeamMember { TeamId = 10, UserId = 1 } }.AsQueryable();
        var teams = new[] { new Team { Id = 10, SpaceId = 5 } }.AsQueryable();
        var scopedRoles = Array.Empty<ScopedUserRole>().AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<ScopedUserRole>(It.IsAny<System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>> pred) =>
                scopedRoles.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 1, spaceId: 99)).ShouldBeFalse(customMessage:
            "User in space 5 must NOT be reported as a member of space 99 — this is the cross-space privesc vector.");
    }

    [Fact]
    public async Task IsMemberAsync_UserNotInAnyTeam_ReturnsFalse()
    {
        // User 99 has no TeamMember rows at all
        var teamMembers = Array.Empty<TeamMember>().AsQueryable();
        var teams = new[] { new Team { Id = 10, SpaceId = 5 } }.AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 99, spaceId: 5)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsMemberAsync_UserInMultipleTeams_AnyMatchingSpace_ReturnsTrue()
    {
        // User in 3 teams; one of them is in the requested space
        var teamMembers = new[]
        {
            new TeamMember { TeamId = 10, UserId = 1 },
            new TeamMember { TeamId = 20, UserId = 1 },
            new TeamMember { TeamId = 30, UserId = 1 },
        }.AsQueryable();
        var teams = new[]
        {
            new Team { Id = 10, SpaceId = 1 },
            new Team { Id = 20, SpaceId = 5 },  // ← match
            new Team { Id = 30, SpaceId = 8 },
        }.AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 1, spaceId: 5)).ShouldBeTrue();
    }

    // ── ScopedUserRole indirect-membership path ──────────────────────────────
    //
    // The seeded "Squid Administrators" team is global (Team.SpaceId = 0)
    // but is granted a SpaceOwner ScopedUserRole in the default Space (SpaceId = 1).
    // Pre-fix the resolver only checked Team.SpaceId == requestedSpaceId, so the
    // admin user could not access the default Space they manifestly own. Path 2
    // adds the ScopedUserRole table to the membership graph so role-grant
    // mappings count as membership for the cross-space gate.

    [Fact]
    public async Task IsMemberAsync_GlobalTeamWithScopedRoleInRequestedSpace_ReturnsTrue()
    {
        // User 1 → Team 10 (SpaceId=0, global) → ScopedUserRole(SpaceId=1, role=SpaceOwner)
        // → user IS a member of Space 1 (the seeded admin scenario)
        var teamMembers = new[] { new TeamMember { TeamId = 10, UserId = 1 } }.AsQueryable();
        var teams = new[] { new Team { Id = 10, SpaceId = 0 } }.AsQueryable();
        var scopedRoles = new[] { new ScopedUserRole { TeamId = 10, UserRoleId = 99, SpaceId = 1 } }.AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<ScopedUserRole>(It.IsAny<System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>> pred) =>
                scopedRoles.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 1, spaceId: 1)).ShouldBeTrue(customMessage:
            "Global team (Team.SpaceId=0) with ScopedUserRole(SpaceId=1) MUST grant membership to Space 1. " +
            "This is the seeded admin path — Squid Administrators is global but owns the default Space via role grant.");
    }

    [Fact]
    public async Task IsMemberAsync_GlobalTeamWithGlobalScopedRole_ReturnsTrueForAnySpace()
    {
        // User 1 → Team 10 → ScopedUserRole(SpaceId=null, role=SystemAdministrator)
        // SpaceId=null on a ScopedUserRole means "system-wide global" — the role applies
        // in every space. Such a user is effectively a member of every space.
        var teamMembers = new[] { new TeamMember { TeamId = 10, UserId = 1 } }.AsQueryable();
        var teams = new[] { new Team { Id = 10, SpaceId = 0 } }.AsQueryable();
        var scopedRoles = new[] { new ScopedUserRole { TeamId = 10, UserRoleId = 1, SpaceId = null } }.AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<ScopedUserRole>(It.IsAny<System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>> pred) =>
                scopedRoles.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 1, spaceId: 42)).ShouldBeTrue(customMessage:
            "ScopedUserRole(SpaceId=null) is a system-wide grant — the user must be reported as a member of " +
            "every space, including Space 42 they have never explicitly been added to. SystemAdministrator role.");
    }

    [Fact]
    public async Task IsMemberAsync_GlobalTeamWithScopedRoleInDifferentSpace_ReturnsFalse()
    {
        // User 1 → Team 10 → ScopedUserRole(SpaceId=2, role=SpaceOwner)
        // User has membership in Space 2 via role grant, but querying Space 99 →
        // FALSE. This is the privesc protection: Path 2 must not over-grant.
        var teamMembers = new[] { new TeamMember { TeamId = 10, UserId = 1 } }.AsQueryable();
        var teams = new[] { new Team { Id = 10, SpaceId = 0 } }.AsQueryable();
        var scopedRoles = new[] { new ScopedUserRole { TeamId = 10, UserRoleId = 99, SpaceId = 2 } }.AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<ScopedUserRole>(It.IsAny<System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>> pred) =>
                scopedRoles.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 1, spaceId: 99)).ShouldBeFalse(customMessage:
            "ScopedUserRole(SpaceId=2) does NOT grant membership to Space 99 — Path 2 must filter by " +
            "the requested space, not over-grant. This pin protects the privesc gate from being weakened.");
    }

    [Fact]
    public async Task IsMemberAsync_GlobalTeamNoScopedRoleAtAll_ReturnsFalse()
    {
        // User 1 → Team 10 (SpaceId=0, global) but the team has NO ScopedUserRole
        // entries — there's no path Direct OR Indirect to any space, so user is not
        // a member of any space. Defensive pin: Path 2 cannot accidentally fall
        // through to "any global team grants any space".
        var teamMembers = new[] { new TeamMember { TeamId = 10, UserId = 1 } }.AsQueryable();
        var teams = new[] { new Team { Id = 10, SpaceId = 0 } }.AsQueryable();
        var scopedRoles = Array.Empty<ScopedUserRole>().AsQueryable();

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<ScopedUserRole>(It.IsAny<System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<ScopedUserRole, bool>> pred) =>
                scopedRoles.Where(pred).BuildMock());

        var resolver = new SpaceMembershipResolver(_repository.Object);

        (await resolver.IsMemberAsync(userId: 1, spaceId: 1)).ShouldBeFalse(customMessage:
            "A global team with NO ScopedUserRole grants must not match Path 2 — global Team.SpaceId=0 " +
            "is necessary but not sufficient for membership; the role-grant edge must exist.");
    }
}
