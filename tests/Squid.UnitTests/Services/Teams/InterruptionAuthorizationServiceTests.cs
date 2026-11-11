using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;

namespace Squid.UnitTests.Services.Teams;

/// <summary>
/// Decision matrix for who may act on (submit / take responsibility for) an interruption. The override
/// branch runs through the REAL <see cref="AuthorizationService"/> over mocked role/team data so the
/// matrix exercises the production space filter, AdministerSystem short-circuit and scope restrictions.
/// </summary>
public class InterruptionAuthorizationServiceTests
{
    private const int UserId = 42;
    private const int InterruptionSpaceId = 1;
    private const int ResponsibleTeamId = 10;
    private const int RoleTeamId = 20;
    private const int ProjectId = 3;
    private const int EnvironmentId = 4;

    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly Mock<IScopedUserRoleDataProvider> _scopedRoles = new();
    private readonly Mock<IUserRoleDataProvider> _roles = new();
    private readonly Mock<IDeploymentDataProvider> _deployments = new();
    private readonly InterruptionAuthorizationService _sut;

    public InterruptionAuthorizationServiceTests()
    {
        var authorizationService = new AuthorizationService(_teamDataProvider.Object, _scopedRoles.Object, _roles.Object);

        _sut = new InterruptionAuthorizationService(_teamDataProvider.Object, authorizationService, _deployments.Object);

        _deployments.Setup(d => d.GetDeploymentByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(new Deployment { Id = 5, ProjectId = ProjectId, EnvironmentId = EnvironmentId });
        _scopedRoles.Setup(s => s.GetProjectScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
        _scopedRoles.Setup(s => s.GetEnvironmentScopeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<int>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc,,")]
    [InlineData(",,,")]
    [InlineData("0,0")]
    public async Task NoEffectiveTeams_IsUnrestricted_WithoutAnyLookup(string responsibleTeamIds)
    {
        var access = await _sut.EnsureCanActAsync(Interruption(responsibleTeamIds), UserId);

        access.ShouldBe(InterruptionAccess.Unrestricted);
        _teamDataProvider.Verify(p => p.IsUserInAnyTeamAsync(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
        _teamDataProvider.Verify(p => p.GetTeamIdsByUserIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TeamMember_ActsAsMember_WithoutConsultingRoles()
    {
        _teamDataProvider.Setup(p => p.IsUserInAnyTeamAsync(UserId, It.Is<List<int>>(ids => ids.Contains(1) && ids.Contains(2)), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var access = await _sut.EnsureCanActAsync(Interruption("1,2"), UserId);

        access.ShouldBe(InterruptionAccess.TeamMember);
        _teamDataProvider.Verify(p => p.GetTeamIdsByUserIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _deployments.Verify(d => d.GetDeploymentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("system-administrator", InterruptionAccess.AdministratorOverride)]
    [InlineData("space-owner-same-space", InterruptionAccess.AdministratorOverride)]
    [InlineData("space-owner-system-scoped", InterruptionAccess.AdministratorOverride)]
    [InlineData("space-owner-scoped-to-this-project", InterruptionAccess.AdministratorOverride)]
    [InlineData("space-owner-another-space", InterruptionAccess.Denied)]
    [InlineData("space-owner-scoped-to-another-project", InterruptionAccess.Denied)]
    [InlineData("space-owner-scoped-to-another-environment", InterruptionAccess.Denied)]
    [InlineData("project-deployer-same-space", InterruptionAccess.Denied)]
    [InlineData("no-roles", InterruptionAccess.Denied)]
    [InlineData("no-teams", InterruptionAccess.Denied)]
    public async Task NonMember_IsAllowedOnlyThroughTheOverridePermissionInTheInterruptionsSpace(string caller, InterruptionAccess expected)
    {
        ArrangeCaller(caller);

        var access = await _sut.GetAccessAsync(Interruption(ResponsibleTeamId.ToString()), UserId);

        access.ShouldBe(expected);
    }

    [Theory]
    [InlineData("space-owner-another-space")]
    [InlineData("project-deployer-same-space")]
    [InlineData("no-teams")]
    public async Task NonMemberWithoutOverride_EnsureCanAct_ThrowsNamingTheOverridePermission(string caller)
    {
        ArrangeCaller(caller);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.EnsureCanActAsync(Interruption(ResponsibleTeamId.ToString()), UserId));

        ex.Message.ShouldContain($"User {UserId} is not a member of any responsible team for this interruption");
        ex.Message.ShouldContain(nameof(Permission.InterruptionSubmitOverride));
    }

    [Fact]
    public async Task NonMemberSpaceOwner_EnsureCanAct_ReturnsAdministratorOverride()
    {
        ArrangeCaller("space-owner-same-space");

        var access = await _sut.EnsureCanActAsync(Interruption(ResponsibleTeamId.ToString()), UserId);

        access.ShouldBe(InterruptionAccess.AdministratorOverride);
    }

    [Fact]
    public async Task OverrideCheck_IsMadeForTheInterruptionsSpaceProjectAndEnvironment()
    {
        var authorization = new Mock<IAuthorizationService>();
        var sut = new InterruptionAuthorizationService(_teamDataProvider.Object, authorization.Object, _deployments.Object);
        authorization.Setup(a => a.CheckPermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(PermissionCheckResult.Authorized());

        await sut.EnsureCanActAsync(Interruption(ResponsibleTeamId.ToString()), UserId);

        authorization.Verify(a => a.CheckPermissionAsync(It.Is<PermissionCheckRequest>(r => r.UserId == UserId && r.Permission == Permission.InterruptionSubmitOverride && r.SpaceId == InterruptionSpaceId && r.ProjectId == ProjectId && r.EnvironmentId == EnvironmentId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OverrideCheck_WhenDeploymentIsGone_StillChecksTheSpaceWithoutProjectScope()
    {
        var authorization = new Mock<IAuthorizationService>();
        var sut = new InterruptionAuthorizationService(_teamDataProvider.Object, authorization.Object, _deployments.Object);
        authorization.Setup(a => a.CheckPermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(PermissionCheckResult.Denied("no"));
        var interruption = Interruption(ResponsibleTeamId.ToString());
        interruption.DeploymentId = 404;

        (await sut.GetAccessAsync(interruption, UserId)).ShouldBe(InterruptionAccess.Denied);

        authorization.Verify(a => a.CheckPermissionAsync(It.Is<PermissionCheckRequest>(r => r.SpaceId == InterruptionSpaceId && r.ProjectId == null && r.EnvironmentId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void OverridePermission_IsSpaceScopedAndGrantedToSpaceOwnerOnly()
    {
        Permission.InterruptionSubmitOverride.GetScope().ShouldBe(PermissionScope.SpaceOnly);
        BuiltInRoles.SpaceOwner.Permissions.ShouldContain(Permission.InterruptionSubmitOverride);
        BuiltInRoles.ProjectDeployer.Permissions.ShouldNotContain(Permission.InterruptionSubmitOverride);
        BuiltInRoles.All.Where(r => r.Permissions.Contains(Permission.InterruptionSubmitOverride)).Select(r => r.Name).ShouldBe([BuiltInRoles.SpaceOwner.Name]);
    }

    [Fact]
    public void OverridePermission_IsAppendedAfterEveryPreExistingPermission()
    {
        // Ordinals of pre-existing permissions must not move: the new value is appended at the end.
        ((int)Permission.AdministerSystem).ShouldBe(54);
        ((int)Permission.InterruptionSubmitOverride).ShouldBe(55);
        Enum.GetValues<Permission>().Max().ShouldBe(Permission.InterruptionSubmitOverride);
    }

    private void ArrangeCaller(string caller)
    {
        _teamDataProvider.Setup(p => p.IsUserInAnyTeamAsync(UserId, It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _teamDataProvider.Setup(p => p.GetTeamIdsByUserIdAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(caller == "no-teams" ? new List<int>() : new List<int> { RoleTeamId });

        var (roleId, spaceId, permissions) = caller switch
        {
            "system-administrator" => (1, (int?)null, BuiltInRoles.SystemAdministrator.Permissions),
            "space-owner-same-space" or "space-owner-scoped-to-this-project" or "space-owner-scoped-to-another-project" or "space-owner-scoped-to-another-environment" => (2, InterruptionSpaceId, BuiltInRoles.SpaceOwner.Permissions),
            "space-owner-system-scoped" => (2, (int?)null, BuiltInRoles.SpaceOwner.Permissions),
            "space-owner-another-space" => (2, 2, BuiltInRoles.SpaceOwner.Permissions),
            "project-deployer-same-space" => (3, InterruptionSpaceId, BuiltInRoles.ProjectDeployer.Permissions),
            _ => (0, (int?)null, new List<Permission>())
        };

        var scopedRoles = roleId == 0 ? new List<ScopedUserRole>() : new List<ScopedUserRole> { new() { Id = 100, TeamId = RoleTeamId, UserRoleId = roleId, SpaceId = spaceId } };

        _scopedRoles.Setup(s => s.GetByTeamIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(scopedRoles);
        _roles.Setup(r => r.GetPermissionsAsync(roleId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions.Select(p => p.ToString()).ToList());

        if (caller == "space-owner-scoped-to-this-project")
            _scopedRoles.Setup(s => s.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { ProjectId });

        if (caller == "space-owner-scoped-to-another-project")
            _scopedRoles.Setup(s => s.GetProjectScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 99 });

        if (caller == "space-owner-scoped-to-another-environment")
            _scopedRoles.Setup(s => s.GetEnvironmentScopeAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(new List<int> { 99 });
    }

    private static DeploymentInterruption Interruption(string responsibleTeamIds)
        => new() { Id = 1, DeploymentId = 5, SpaceId = InterruptionSpaceId, ResponsibleTeamIds = responsibleTeamIds };
}
