using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Authorization;

public class AuthorizationGrantConsistencyTests : TestBase
{
    private const int UserId = 8101;
    private const int TeamId = 8201;
    private const int RoleId = 8301;
    private const int ScopedRoleId = 8401;

    public AuthorizationGrantConsistencyTests()
        : base("AuthorizationGrantConsistency", "squid_it_authorization_grant_consistency")
    {
    }

    [Fact]
    public async Task Grants_ReadOneRowPerEligibleRoleInSingleSelect()
    {
        await SeedGrantAsync().ConfigureAwait(false);
        var commands = new AuthorizationSelectCapture();

        var grants = await Run<IAuthorizationService, IReadOnlyList<ResourceScopeGrant>>(
            service => service.GetResourceScopeGrantsAsync(UserId, Permission.ProjectView),
            await commands.CreateRegistrationAsync(CurrentScope)).ConfigureAwait(false);

        commands.Selects.ShouldHaveSingleItem().RowCount.ShouldBe(1);
        var grant = grants.ShouldHaveSingleItem();
        grant.SpaceId.ShouldBe(1);
        grant.ProjectIds.ShouldBe(new HashSet<int> { 1001, 1002 });
        grant.EnvironmentIds.ShouldBe(new HashSet<int> { 2001, 2002 });
        grant.ProjectGroupIds.ShouldBe(new HashSet<int> { 3001, 3002 });
    }

    [Fact]
    public async Task Grants_AreEmptyAfterUserRoleDeletion()
    {
        await SeedGrantAsync().ConfigureAwait(false);

        await Run<IUserRoleDataProvider>(provider =>
            provider.DeleteAsync(new UserRole { Id = RoleId })).ConfigureAwait(false);

        var grants = await Run<IAuthorizationService, IReadOnlyList<ResourceScopeGrant>>(
            service => service.GetResourceScopeGrantsAsync(UserId, Permission.ProjectView)).ConfigureAwait(false);

        grants.ShouldBeEmpty();
    }

    private async Task SeedGrantAsync()
    {
        await Run<SquidDbContext>(async db =>
        {
            db.Set<Team>().Add(new Team
            {
                Id = TeamId,
                Name = "Grant Consistency Team",
                Description = string.Empty,
                SpaceId = 1,
                IsBuiltIn = false
            });
            db.Set<UserRole>().Add(new UserRole
            {
                Id = RoleId,
                Name = "Grant Consistency Role",
                Description = string.Empty,
                IsBuiltIn = false
            });
            await db.SaveChangesAsync();

            db.Set<TeamMember>().Add(new TeamMember { TeamId = TeamId, UserId = UserId });
            db.Set<UserRolePermission>().Add(new UserRolePermission
            {
                UserRoleId = RoleId,
                Permission = Permission.ProjectView.ToString()
            });
            db.Set<ScopedUserRole>().Add(new ScopedUserRole
            {
                Id = ScopedRoleId,
                TeamId = TeamId,
                UserRoleId = RoleId,
                SpaceId = 1
            });
            await db.SaveChangesAsync();

            db.Set<ScopedUserRoleProject>().AddRange(
                new ScopedUserRoleProject { ScopedUserRoleId = ScopedRoleId, ProjectId = 1001 },
                new ScopedUserRoleProject { ScopedUserRoleId = ScopedRoleId, ProjectId = 1002 });
            db.Set<ScopedUserRoleEnvironment>().AddRange(
                new ScopedUserRoleEnvironment { ScopedUserRoleId = ScopedRoleId, EnvironmentId = 2001 },
                new ScopedUserRoleEnvironment { ScopedUserRoleId = ScopedRoleId, EnvironmentId = 2002 });
            db.Set<ScopedUserRoleProjectGroup>().AddRange(
                new ScopedUserRoleProjectGroup { ScopedUserRoleId = ScopedRoleId, ProjectGroupId = 3001 },
                new ScopedUserRoleProjectGroup { ScopedUserRoleId = ScopedRoleId, ProjectGroupId = 3002 });
            await db.SaveChangesAsync();
        }).ConfigureAwait(false);
    }
}
