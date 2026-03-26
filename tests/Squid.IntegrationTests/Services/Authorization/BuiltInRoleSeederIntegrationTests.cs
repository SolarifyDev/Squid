using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Teams;
using Squid.Message.Constants;

namespace Squid.IntegrationTests.Services.Authorization;

public class BuiltInRoleSeederIntegrationTests : TestBase
{
    public BuiltInRoleSeederIntegrationTests()
        : base("BuiltInRoleSeeder", "squid_it_builtin_role_seeder")
    {
    }

    [Fact]
    public void DataSeederRunner_RegisteredAsStartable()
    {
        var startables = CurrentScope.Resolve<IEnumerable<IStartable>>();

        startables.OfType<DataSeederRunner>().ShouldNotBeEmpty("DataSeederRunner must be registered as IStartable in the DI container");
    }

    [Fact]
    public void AllDataSeeders_RegisteredInCorrectOrder()
    {
        var seeders = CurrentScope.Resolve<IEnumerable<IDataSeeder>>().OrderBy(s => s.Order).ToList();

        seeders.Count.ShouldBeGreaterThanOrEqualTo(5);
        seeders[0].Order.ShouldBe(100);
        seeders[1].Order.ShouldBe(200);
        seeders[2].Order.ShouldBe(300);
        seeders[3].Order.ShouldBe(400);
        seeders[4].Order.ShouldBe(500);
    }

    [Fact]
    public async Task SeedRoles_CreatesAllBuiltInRoles()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var roles = await provider.GetAllAsync().ConfigureAwait(false);
            roles.Count.ShouldBeGreaterThanOrEqualTo(BuiltInRoles.All.Count);

            foreach (var definition in BuiltInRoles.All)
            {
                var role = await provider.GetByNameAsync(definition.Name).ConfigureAwait(false);
                role.ShouldNotBeNull($"Built-in role '{definition.Name}' should exist");
                role.IsBuiltIn.ShouldBeTrue();

                var permissions = await provider.GetPermissionsAsync(role.Id).ConfigureAwait(false);
                permissions.Count.ShouldBe(definition.Permissions.Count);
            }
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRoles_Idempotent_DoesNotDuplicate()
    {
        await RunDataSeeders().ConfigureAwait(false);
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var roles = await provider.GetAllAsync().ConfigureAwait(false);
            var builtInCount = roles.Count(r => r.IsBuiltIn);
            builtInCount.ShouldBe(BuiltInRoles.All.Count);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRoles_CreatesDefaultTeams()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<ITeamDataProvider, IScopedUserRoleDataProvider>(async (teamProvider, scopedProvider) =>
        {
            var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);

            var adminTeam = teams.FirstOrDefault(t => t.Name == "Squid Administrators");
            adminTeam.ShouldNotBeNull("Squid Administrators team should exist");

            var everyoneTeam = teams.FirstOrDefault(t => t.Name == "Everyone");
            everyoneTeam.ShouldNotBeNull("Everyone team should exist");

            // Admin team should have the System Administrator role assigned
            var adminRoles = await scopedProvider.GetByTeamIdsAsync(new List<int> { adminTeam.Id }).ConfigureAwait(false);
            adminRoles.Count.ShouldBeGreaterThanOrEqualTo(1);

            // Both built-in teams should have IsBuiltIn set
            adminTeam.IsBuiltIn.ShouldBeTrue();
            everyoneTeam.IsBuiltIn.ShouldBeTrue();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRoles_CreatesAdminUser()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IRepository>(async repository =>
        {
            var normalizedName = CurrentUsers.AdminUser.UserName.ToUpperInvariant();
            var admin = await repository.FirstOrDefaultAsync<UserAccount>(x => x.NormalizedUserName == normalizedName).ConfigureAwait(false);

            admin.ShouldNotBeNull("Admin user should exist after seeding");
            admin.UserName.ShouldBe(CurrentUsers.AdminUser.UserName);
            admin.DisplayName.ShouldBe(CurrentUsers.AdminUser.DisplayName);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRoles_AdminInAdministratorsAndEveryoneTeams()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IRepository, ITeamDataProvider>(async (repository, teamProvider) =>
        {
            var normalizedName = CurrentUsers.AdminUser.UserName.ToUpperInvariant();
            var admin = await repository.FirstOrDefaultAsync<UserAccount>(x => x.NormalizedUserName == normalizedName).ConfigureAwait(false);

            admin.ShouldNotBeNull();

            var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);

            var adminTeam = teams.First(t => t.Name == "Squid Administrators");
            var adminMembers = await teamProvider.GetMembersByTeamIdAsync(adminTeam.Id).ConfigureAwait(false);
            adminMembers.ShouldContain(m => m.UserId == admin.Id, "Admin should be in Squid Administrators team");

            var everyoneTeam = teams.First(t => t.Name == "Everyone");
            var everyoneMembers = await teamProvider.GetMembersByTeamIdAsync(everyoneTeam.Id).ConfigureAwait(false);
            everyoneMembers.ShouldContain(m => m.UserId == admin.Id, "Admin should be in Everyone team");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRoles_CreatesDefaultSpaceWithOwnerTeam()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<IRepository, ITeamDataProvider>(async (repository, teamProvider) =>
        {
            var defaultSpace = await repository.FirstOrDefaultAsync<Space>(s => s.IsDefault).ConfigureAwait(false);

            defaultSpace.ShouldNotBeNull("Default space should exist after seeding");
            defaultSpace.Name.ShouldBe("Default");

            var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
            var adminTeam = teams.First(t => t.Name == "Squid Administrators");

            defaultSpace.OwnerTeamId.ShouldBe(adminTeam.Id, "Default space OwnerTeamId should point to Administrators team");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRoles_AdminTeamHasSpaceOwnerInDefaultSpace()
    {
        await RunDataSeeders().ConfigureAwait(false);

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider, IRepository>(async (teamProvider, roleProvider, scopedProvider, repository) =>
        {
            var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
            var adminTeam = teams.First(t => t.Name == "Squid Administrators");

            var spaceOwnerRole = await roleProvider.GetByNameAsync("Space Owner").ConfigureAwait(false);
            spaceOwnerRole.ShouldNotBeNull();

            var defaultSpace = await repository.FirstOrDefaultAsync<Space>(s => s.IsDefault).ConfigureAwait(false);
            defaultSpace.ShouldNotBeNull();

            var adminScopedRoles = await scopedProvider.GetByTeamIdsAsync(new List<int> { adminTeam.Id }).ConfigureAwait(false);
            adminScopedRoles.ShouldContain(sr => sr.UserRoleId == spaceOwnerRole.Id && sr.SpaceId == defaultSpace.Id, "Administrators should have Space Owner role scoped to Default Space");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SeedRoles_Idempotent_NoDuplicateSpaceOwnerAssignment()
    {
        await RunDataSeeders().ConfigureAwait(false);
        await RunDataSeeders().ConfigureAwait(false);

        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider, IRepository>(async (teamProvider, roleProvider, scopedProvider, repository) =>
        {
            var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
            var adminTeam = teams.First(t => t.Name == "Squid Administrators");

            var spaceOwnerRole = await roleProvider.GetByNameAsync("Space Owner").ConfigureAwait(false);
            spaceOwnerRole.ShouldNotBeNull();

            var defaultSpace = await repository.FirstOrDefaultAsync<Space>(s => s.IsDefault).ConfigureAwait(false);
            defaultSpace.ShouldNotBeNull();

            var adminScopedRoles = await scopedProvider.GetByTeamIdsAsync(new List<int> { adminTeam.Id }).ConfigureAwait(false);
            var spaceOwnerCount = adminScopedRoles.Count(sr => sr.UserRoleId == spaceOwnerRole.Id && sr.SpaceId == defaultSpace.Id);
            spaceOwnerCount.ShouldBe(1, "Running seed twice should not create duplicate Space Owner assignments");
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
