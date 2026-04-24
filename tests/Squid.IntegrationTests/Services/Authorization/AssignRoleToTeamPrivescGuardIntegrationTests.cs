using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Authorization;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Authorization;

/// <summary>
/// P0-D.2 regression guard (2026-04-24 audit). End-to-end exercise of the privilege-
/// escalation guard in <c>UserRoleService.AssignRoleToTeamAsync</c>. The unit tests in
/// <c>Squid.UnitTests.Services.Authorization.UserRoleServiceTests</c> cover the
/// decision matrix with mocks; these tests verify the same guard fires through the
/// real DI graph + real EF-backed data providers.
///
/// <para>Integration coverage matters here because the guard depends on three things
/// being wired correctly at composition time: <c>IAuthorizationService</c> must return
/// a sensible answer against the real scoped-role tables; <c>ICurrentUser</c> must be
/// injected into <c>UserRoleService</c>; and the mediator-free entry-point (service
/// method call) must still run the check. A DI mis-wire that dropped <c>ICurrentUser</c>
/// would be invisible in mock tests.</para>
/// </summary>
public class AssignRoleToTeamPrivescGuardIntegrationTests : TestBase
{
    public AssignRoleToTeamPrivescGuardIntegrationTests()
        : base("AssignRoleToTeamPrivescGuard", "squid_it_assign_role_privesc_guard")
    {
    }

    private sealed class FixedCurrentUser : ICurrentUser
    {
        public int? Id { get; init; }
        public string Name => "integration-test-user";
    }

    [Fact]
    public async Task CallerWithoutAdministerSystem_AssigningRoleContainingAdministerSystem_Throws()
    {
        // Arrange — victim team + malicious role containing AdministerSystem.
        var teamId = 0;
        var adminRoleId = 0;

        await Run<ITeamDataProvider, IUserRoleDataProvider>(async (teamProvider, roleProvider) =>
        {
            var team = new Team { Name = "VictimTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;

            var role = new UserRole { Name = "BadAdmin", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { nameof(Permission.AdministerSystem) }).ConfigureAwait(false);
            adminRoleId = role.Id;
        }).ConfigureAwait(false);

        // Act + Assert — caller (user 42) has NO scoped roles in the DB so they
        // don't hold AdministerSystem. The guard must refuse.
        await Run<IUserRoleService>(
            async userRoleService =>
            {
                var command = new AssignRoleToTeamCommand { TeamId = teamId, UserRoleId = adminRoleId, SpaceId = null };

                var ex = await Should.ThrowAsync<InvalidOperationException>(
                    () => userRoleService.AssignRoleToTeamAsync(command)).ConfigureAwait(false);

                ex.Message.ShouldContain("AdministerSystem",
                    customMessage: "error must name the required caller-side permission");
            },
            b => b.RegisterInstance<ICurrentUser>(new FixedCurrentUser { Id = 42 }).SingleInstance()).ConfigureAwait(false);
    }

    [Fact]
    public async Task CallerWithAdministerSystem_AssigningRoleContainingAdministerSystem_Succeeds()
    {
        const int adminUserId = 77;
        var targetTeamId = 0;
        var assignableAdminRoleId = 0;

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            // Caller has AdministerSystem at system level — fully authorized.
            var adminTeam = new Team { Name = "AdminTeam", SpaceId = 1 };
            await teamProvider.AddAsync(adminTeam).ConfigureAwait(false);
            await teamProvider.AddMemberAsync(new TeamMember { TeamId = adminTeam.Id, UserId = adminUserId }).ConfigureAwait(false);

            var callerAdminRole = new UserRole { Name = "CallerAdmin", IsBuiltIn = false };
            await roleProvider.AddAsync(callerAdminRole).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(callerAdminRole.Id, new List<string> { nameof(Permission.AdministerSystem) }).ConfigureAwait(false);

            await scopedProvider.AddAsync(new ScopedUserRole { TeamId = adminTeam.Id, UserRoleId = callerAdminRole.Id, SpaceId = null }).ConfigureAwait(false);

            // Target team + role that the caller is going to assign.
            var target = new Team { Name = "TargetTeam", SpaceId = 1 };
            await teamProvider.AddAsync(target).ConfigureAwait(false);
            targetTeamId = target.Id;

            var assignable = new UserRole { Name = "AnotherSystemAdmin", IsBuiltIn = false };
            await roleProvider.AddAsync(assignable).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(assignable.Id, new List<string> { nameof(Permission.AdministerSystem) }).ConfigureAwait(false);
            assignableAdminRoleId = assignable.Id;
        }).ConfigureAwait(false);

        await Run<IUserRoleService>(
            async userRoleService =>
            {
                var command = new AssignRoleToTeamCommand { TeamId = targetTeamId, UserRoleId = assignableAdminRoleId, SpaceId = null };

                // No throw — admin CAN delegate AdministerSystem to another team.
                var dto = await userRoleService.AssignRoleToTeamAsync(command).ConfigureAwait(false);

                dto.ShouldNotBeNull();
                dto.TeamId.ShouldBe(targetTeamId);
                dto.UserRoleId.ShouldBe(assignableAdminRoleId);
            },
            b => b.RegisterInstance<ICurrentUser>(new FixedCurrentUser { Id = adminUserId }).SingleInstance()).ConfigureAwait(false);
    }

    [Fact]
    public async Task CallerWithoutAdministerSystem_AssigningSpaceOnlyRole_Succeeds()
    {
        // Counterpoint — the guard must NOT break the legitimate "TeamEdit holder
        // assigns a space-level role" workflow. Only SystemOnly-containing roles
        // invoke the AdministerSystem check.
        var teamId = 0;
        var projectViewerRoleId = 0;

        await Run<ITeamDataProvider, IUserRoleDataProvider>(async (teamProvider, roleProvider) =>
        {
            var team = new Team { Name = "DevTeam", SpaceId = 1 };
            await teamProvider.AddAsync(team).ConfigureAwait(false);
            teamId = team.Id;

            var role = new UserRole { Name = "ProjectViewer", IsBuiltIn = false };
            await roleProvider.AddAsync(role).ConfigureAwait(false);
            await roleProvider.SetPermissionsAsync(role.Id, new List<string> { nameof(Permission.ProjectView) }).ConfigureAwait(false);
            projectViewerRoleId = role.Id;
        }).ConfigureAwait(false);

        await Run<IUserRoleService>(
            async userRoleService =>
            {
                var command = new AssignRoleToTeamCommand { TeamId = teamId, UserRoleId = projectViewerRoleId, SpaceId = 1 };

                var dto = await userRoleService.AssignRoleToTeamAsync(command).ConfigureAwait(false);

                dto.ShouldNotBeNull();
                dto.UserRoleId.ShouldBe(projectViewerRoleId);
            },
            b => b.RegisterInstance<ICurrentUser>(new FixedCurrentUser { Id = 42 }).SingleInstance()).ConfigureAwait(false);
    }
}
