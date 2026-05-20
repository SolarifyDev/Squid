using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Teams;
using Squid.Message.Constants;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Authorization;

/// <summary>
/// Pins the InternalUser bootstrap surface in DB form: user_account row 8888 exists,
/// the "Internal Service Accounts" team owns the SystemServiceAccount role cross-space,
/// and user 8888 is a member of that team. Together this is what makes a Tentacle
/// register call (carrying an InternalUser-owned API key) actually pass the
/// MachineCreate permission check.
/// </summary>
public class InternalUserSeederIntegrationTests : TestBase
{
    public InternalUserSeederIntegrationTests()
        : base("InternalUserSeeder", "squid_it_internal_user_seeder")
    {
    }

    [Fact]
    public async Task SeedRuns_CreatesInternalUserAccountWithCanonicalId()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var user = await repository.FirstOrDefaultAsync<UserAccount>(x => x.Id == CurrentUsers.InternalUser.Id).ConfigureAwait(false);

            user.ShouldNotBeNull(customMessage: "InternalUser (id=8888) must exist after seeding.");
            user.UserName.ShouldBe(CurrentUsers.InternalUser.Name);
            user.DisplayName.ShouldBe(CurrentUsers.InternalUser.DisplayName);
            user.IsSystem.ShouldBeTrue(customMessage: "InternalUser must have IsSystem=true so the UI can hide it from human-user lists.");
            user.IsDisabled.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRuns_CreatesInternalServiceAccountsTeam_InSpaceZero()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<ITeamDataProvider>(async teamProvider =>
        {
            var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
            var team = teams.FirstOrDefault(t => t.Name == "Internal Service Accounts");

            team.ShouldNotBeNull(customMessage: "Internal Service Accounts team must exist in space 0 (system).");
            team.IsBuiltIn.ShouldBeTrue();
            team.Description.ShouldContain("Do NOT add human users",
                customMessage: "Team description must warn against human assignment.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRuns_AssignsSystemServiceAccountRoleToTeam_CrossSpace()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, roleProvider, scopedProvider) =>
        {
            var team = (await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false)).First(t => t.Name == "Internal Service Accounts");
            var role = await roleProvider.GetByNameAsync(BuiltInRoles.SystemServiceAccount.Name).ConfigureAwait(false);

            role.ShouldNotBeNull();

            var scopedRoles = await scopedProvider.GetByTeamIdsAsync(new List<int> { team.Id }).ConfigureAwait(false);
            var assignment = scopedRoles.FirstOrDefault(sr => sr.UserRoleId == role.Id);

            assignment.ShouldNotBeNull(customMessage: "SystemServiceAccount role must be assigned to the Internal Service Accounts team.");
            assignment.SpaceId.ShouldBeNull(customMessage:
                "SpaceId must be null (cross-space) so the InternalUser can register Tentacles in any space. " +
                "A non-null SpaceId would limit the bootstrap to that single space.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRuns_AddsInternalUserToTeam()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<ITeamDataProvider>(async teamProvider =>
        {
            var team = (await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false)).First(t => t.Name == "Internal Service Accounts");
            var members = await teamProvider.GetMembersByTeamIdAsync(team.Id).ConfigureAwait(false);

            members.ShouldContain(m => m.UserId == CurrentUsers.InternalUser.Id,
                customMessage: "InternalUser must be a member of the Internal Service Accounts team to inherit the role.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRuns_InternalUserHasMachineCreatePermission_ViaAuthorizationService()
    {
        // End-to-end pin: walk the full permission resolution path the way the
        // register endpoint does. If this passes, a Tentacle register call carrying
        // an InternalUser-owned API key WILL pass the MachineCreate authorization check.
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IAuthorizationService>(async authzService =>
        {
            var result = await authzService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = CurrentUsers.InternalUser.Id,
                Permission = Permission.MachineCreate,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            result.IsAuthorized.ShouldBeTrue(customMessage:
                "InternalUser MUST have MachineCreate permission in default space (SpaceId=1) -- this is the " +
                "whole point of the bootstrap-key redesign. If this fails, the install script's register call " +
                "will hit 403 every time. Trace: SystemServiceAccount role → ScopedUserRole (SpaceId=null) → " +
                "team membership → user 8888.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRuns_InternalUserDoesNotHaveMachineDelete()
    {
        // Least-privilege pin: the bootstrap surface must NOT include Delete.
        // If a leaked bootstrap key could also delete machines, blast radius is huge.
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IAuthorizationService>(async authzService =>
        {
            var result = await authzService.CheckPermissionAsync(new PermissionCheckRequest
            {
                UserId = CurrentUsers.InternalUser.Id,
                Permission = Permission.MachineDelete,
                SpaceId = 1
            }, CancellationToken.None).ConfigureAwait(false);

            result.IsAuthorized.ShouldBeFalse(customMessage:
                "InternalUser MUST NOT have MachineDelete -- bootstrap key blast radius must stay narrow. " +
                "If this assertion now fails, SystemServiceAccount role was widened OR a new role assignment leaked in.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRuns_Idempotent_NoDuplicateUserOrTeamOrAssignment()
    {
        await RunDataSeeders().ConfigureAwait(false);
        await RunDataSeeders().ConfigureAwait(false);
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IRepository, ITeamDataProvider, IScopedUserRoleDataProvider, IUserRoleDataProvider>(async (repository, teamProvider, scopedProvider, roleProvider) =>
        {
            var users = await repository.ToListAsync<UserAccount>(u => u.Id == CurrentUsers.InternalUser.Id).ConfigureAwait(false);
            users.Count.ShouldBe(1, customMessage: "Running seeder N times must NOT duplicate the InternalUser row.");

            var teams = (await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false)).Where(t => t.Name == "Internal Service Accounts").ToList();
            teams.Count.ShouldBe(1, customMessage: "Internal Service Accounts team must not be duplicated.");

            var members = await teamProvider.GetMembersByTeamIdAsync(teams[0].Id).ConfigureAwait(false);
            members.Count(m => m.UserId == CurrentUsers.InternalUser.Id).ShouldBe(1, customMessage: "team_member row must not duplicate.");

            var role = await roleProvider.GetByNameAsync(BuiltInRoles.SystemServiceAccount.Name).ConfigureAwait(false);
            var scopedRoles = await scopedProvider.GetByTeamIdsAsync(new List<int> { teams[0].Id }).ConfigureAwait(false);
            scopedRoles.Count(sr => sr.UserRoleId == role.Id).ShouldBe(1, customMessage: "scoped_user_role assignment must not duplicate.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRuns_RegisteredAtOrder350()
    {
        // Ordering pin: InternalUserSeeder must run AFTER BuiltInRoleSeeder (100) so the
        // SystemServiceAccount role row exists when we look it up, AFTER BuiltInTeamSeeder
        // (200), AFTER AdminUserSeeder (300) so audit columns reference a real user.
        var seeder = new InternalUserSeeder();
        seeder.Order.ShouldBe(350);
    }

    [Fact]
    public async Task BootstrapKeyRotation_EndToEnd_GenerateScriptAfterRotation_GetsFreshKey()
    {
        // End-to-end pin: rotation invalidates the previously-shared bootstrap key.
        // The next FindApiKeyByDescriptionAsync MUST return null so the next
        // GenerateInstallScript call mints a fresh row. Without this, rotation would
        // leave the leaked key still in use and the rotation endpoint would be
        // useless.
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IAccountService>(async accountService =>
        {
            // Mint the initial shared key (simulating first GenerateInstallScript).
            var firstKey = await accountService.CreateApiKeyAsync(
                CurrentUsers.InternalUser.Id,
                MachineScriptService.TentacleBootstrapKeyDescription,
                CancellationToken.None).ConfigureAwait(false);

            var found = await accountService.FindApiKeyByDescriptionAsync(
                CurrentUsers.InternalUser.Id,
                MachineScriptService.TentacleBootstrapKeyDescription,
                CancellationToken.None).ConfigureAwait(false);

            found.ShouldNotBeNull();
            found.ApiKey.ShouldBe(firstKey.ApiKey,
                customMessage: "Before rotation, Find returns the shared key minted above.");

            // Rotate -- disables the existing key.
            var disabledCount = await accountService.DisableApiKeysByDescriptionAsync(
                CurrentUsers.InternalUser.Id,
                MachineScriptService.TentacleBootstrapKeyDescription,
                CancellationToken.None).ConfigureAwait(false);

            disabledCount.ShouldBe(1);

            // After rotation, Find returns null -- next GenerateInstallScript would mint fresh.
            var afterRotation = await accountService.FindApiKeyByDescriptionAsync(
                CurrentUsers.InternalUser.Id,
                MachineScriptService.TentacleBootstrapKeyDescription,
                CancellationToken.None).ConfigureAwait(false);

            afterRotation.ShouldBeNull(customMessage:
                "After rotation, the shared bootstrap key MUST be disabled. The next install-script generation " +
                "will mint a fresh one via the get-or-create flow. If this returns non-null, rotation is broken " +
                "and operators can't actually invalidate a leaked key.");
        }).ConfigureAwait(false);
    }

    private async Task RunDataSeeders()
    {
        await Run<ILifetimeScope>(async scope =>
        {
            var runner = new DataSeederRunner(scope);
            runner.Start();

            await Task.CompletedTask;
        }).ConfigureAwait(false);
    }
}
