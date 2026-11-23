using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Message.Commands.Authorization;

namespace Squid.IntegrationTests.Services.Authorization;

public class UserRoleServiceScopeUpdateTests : TestBase
{
    private const int TeamId = 8501;
    private const int RoleId = 8601;
    private const int ScopedRoleId = 8701;
    private const int ExistingProjectId = 8801;
    private const int ReplacementProjectId = 8802;
    private const int FailingProjectGroupId = 899999;
    private const int ExistingEnvironmentId = 8901;
    private const int ReplacementEnvironmentId = 8902;
    private const int ExistingProjectGroupId = 9001;
    private const int ReplacementProjectGroupId = 9002;

    public UserRoleServiceScopeUpdateTests()
        : base("UserRoleServiceScopeUpdate", "squid_it_user_role_scope_update")
    {
    }

    [Fact]
    public async Task UpdateRoleScope_ReplacesAllScopes()
    {
        await SeedRoleWithScopesAsync().ConfigureAwait(false);
        var command = new UpdateRoleScopeCommand
        {
            TeamId = TeamId,
            ScopedUserRoleId = ScopedRoleId,
            ProjectIds = [ReplacementProjectId],
            EnvironmentIds = [ReplacementEnvironmentId],
            ProjectGroupIds = [ExistingProjectGroupId, ReplacementProjectGroupId]
        };

        await Run<IUserRoleService>(service => service.UpdateRoleScopeAsync(command)).ConfigureAwait(false);

        await Run<SquidDbContext>(async db =>
        {
            var projectIds = await db.Set<ScopedUserRoleProject>()
                .Where(scope => scope.ScopedUserRoleId == ScopedRoleId)
                .Select(scope => scope.ProjectId)
                .ToListAsync();
            var environmentIds = await db.Set<ScopedUserRoleEnvironment>()
                .Where(scope => scope.ScopedUserRoleId == ScopedRoleId)
                .Select(scope => scope.EnvironmentId)
                .ToListAsync();
            var projectGroupIds = await db.Set<ScopedUserRoleProjectGroup>()
                .Where(scope => scope.ScopedUserRoleId == ScopedRoleId)
                .Select(scope => scope.ProjectGroupId)
                .OrderBy(id => id)
                .ToListAsync();

            projectIds.ShouldBe([ReplacementProjectId]);
            environmentIds.ShouldBe([ReplacementEnvironmentId]);
            projectGroupIds.ShouldBe([ExistingProjectGroupId, ReplacementProjectGroupId]);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task UpdateRoleScope_OnWriteFailure_LeavesAllScopesUnchanged()
    {
        await SeedRoleWithScopesAsync().ConfigureAwait(false);
        await InstallFailureTriggerAsync().ConfigureAwait(false);

        try
        {
            var command = new UpdateRoleScopeCommand
            {
                TeamId = TeamId,
                ScopedUserRoleId = ScopedRoleId,
                ProjectIds = [ReplacementProjectId],
                EnvironmentIds = [ReplacementEnvironmentId],
                ProjectGroupIds = [ExistingProjectGroupId, FailingProjectGroupId]
            };

            await Should.ThrowAsync<DbUpdateException>(() =>
                Run<IUserRoleService>(service => service.UpdateRoleScopeAsync(command))).ConfigureAwait(false);
        }
        finally
        {
            await RemoveFailureTriggerAsync().ConfigureAwait(false);
        }

        await Run<SquidDbContext>(async db =>
        {
            var projectIds = await db.Set<ScopedUserRoleProject>()
                .Where(scope => scope.ScopedUserRoleId == ScopedRoleId)
                .Select(scope => scope.ProjectId)
                .OrderBy(id => id)
                .ToListAsync();
            var environmentIds = await db.Set<ScopedUserRoleEnvironment>()
                .Where(scope => scope.ScopedUserRoleId == ScopedRoleId)
                .Select(scope => scope.EnvironmentId)
                .ToListAsync();
            var projectGroupIds = await db.Set<ScopedUserRoleProjectGroup>()
                .Where(scope => scope.ScopedUserRoleId == ScopedRoleId)
                .Select(scope => scope.ProjectGroupId)
                .ToListAsync();

            projectIds.ShouldBe([ExistingProjectId]);
            environmentIds.ShouldBe([ExistingEnvironmentId]);
            projectGroupIds.ShouldBe([ExistingProjectGroupId]);
        }).ConfigureAwait(false);
    }

    private async Task SeedRoleWithScopesAsync()
    {
        await Run<SquidDbContext>(async db =>
        {
            db.Set<Team>().Add(new Team
            {
                Id = TeamId,
                Name = "Scope Update Team",
                Description = string.Empty,
                SpaceId = 1,
                IsBuiltIn = false
            });
            db.Set<UserRole>().Add(new UserRole
            {
                Id = RoleId,
                Name = "Scope Update Role",
                Description = string.Empty,
                IsBuiltIn = false
            });
            await db.SaveChangesAsync();

            db.Set<ScopedUserRole>().Add(new ScopedUserRole
            {
                Id = ScopedRoleId,
                TeamId = TeamId,
                UserRoleId = RoleId,
                SpaceId = 1
            });
            await db.SaveChangesAsync();

            db.Set<ScopedUserRoleProject>().Add(new ScopedUserRoleProject
            {
                ScopedUserRoleId = ScopedRoleId,
                ProjectId = ExistingProjectId
            });
            db.Set<ScopedUserRoleEnvironment>().Add(new ScopedUserRoleEnvironment
            {
                ScopedUserRoleId = ScopedRoleId,
                EnvironmentId = ExistingEnvironmentId
            });
            db.Set<ScopedUserRoleProjectGroup>().Add(new ScopedUserRoleProjectGroup
            {
                ScopedUserRoleId = ScopedRoleId,
                ProjectGroupId = ExistingProjectGroupId
            });
            await db.SaveChangesAsync();
        }).ConfigureAwait(false);
    }

    private Task InstallFailureTriggerAsync()
    {
        return Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync($$"""
            CREATE OR REPLACE FUNCTION squid_test_fail_scope_insert()
            RETURNS trigger AS $$
            BEGIN
                IF NEW.project_group_id = {{FailingProjectGroupId}} THEN
                    RAISE EXCEPTION 'synthetic scope update failure';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS squid_test_fail_scope_insert ON scoped_user_role_project_group;
            CREATE TRIGGER squid_test_fail_scope_insert
            BEFORE INSERT ON scoped_user_role_project_group
            FOR EACH ROW EXECUTE FUNCTION squid_test_fail_scope_insert();
            """));
    }

    private Task RemoveFailureTriggerAsync()
    {
        return Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("""
            DROP TRIGGER IF EXISTS squid_test_fail_scope_insert ON scoped_user_role_project_group;
            DROP FUNCTION IF EXISTS squid_test_fail_scope_insert();
            """));
    }
}
