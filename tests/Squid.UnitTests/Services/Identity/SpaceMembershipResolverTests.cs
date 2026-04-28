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
/// <c>X-Space-Id: 2</c>. Pre-Phase-10.3, the SpaceIdInjectionSpecification
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

        _repository.Setup(r => r.Query<TeamMember>(It.IsAny<System.Linq.Expressions.Expression<System.Func<TeamMember, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<TeamMember, bool>> pred) =>
                teamMembers.Where(pred).BuildMock());
        _repository.Setup(r => r.Query<Team>(It.IsAny<System.Linq.Expressions.Expression<System.Func<Team, bool>>>()))
            .Returns((System.Linq.Expressions.Expression<System.Func<Team, bool>> pred) =>
                teams.Where(pred).BuildMock());

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
}
