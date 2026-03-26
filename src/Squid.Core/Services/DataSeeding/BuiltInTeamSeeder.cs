using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;

namespace Squid.Core.Services.DataSeeding;

public class BuiltInTeamSeeder : IDataSeeder
{
    public int Order => 200;

    public async Task SeedAsync(ILifetimeScope scope)
    {
        var teamProvider = scope.Resolve<ITeamDataProvider>();
        var roleProvider = scope.Resolve<IUserRoleDataProvider>();
        var scopedRoleProvider = scope.Resolve<IScopedUserRoleDataProvider>();

        await SeedTeamWithRoleAsync(teamProvider, roleProvider, scopedRoleProvider, "Squid Administrators", "Built-in administrators team", BuiltInRoles.SystemAdministrator.Name).ConfigureAwait(false);
        await SeedTeamAsync(teamProvider, "Everyone", "All users").ConfigureAwait(false);

        Log.Information("Built-in team seeding complete");
    }

    private static async Task SeedTeamWithRoleAsync(ITeamDataProvider teamProvider, IUserRoleDataProvider roleProvider, IScopedUserRoleDataProvider scopedRoleProvider, string teamName, string description, string roleName)
    {
        var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
        var team = teams.FirstOrDefault(t => t.Name == teamName);

        if (team == null)
        {
            team = new Team { Name = teamName, Description = description, SpaceId = 0, IsBuiltIn = true };
            await teamProvider.AddAsync(team).ConfigureAwait(false);

            Log.Information("Seeded default team {TeamName}", teamName);
        }

        var role = await roleProvider.GetByNameAsync(roleName).ConfigureAwait(false);
        if (role == null) return;

        var existingScopedRoles = await scopedRoleProvider.GetByTeamIdsAsync(new List<int> { team.Id }).ConfigureAwait(false);

        if (existingScopedRoles.Any(sr => sr.UserRoleId == role.Id))
            return;

        await scopedRoleProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = null }).ConfigureAwait(false);

        Log.Information("Assigned role {RoleName} to team {TeamName}", roleName, teamName);
    }

    private static async Task SeedTeamAsync(ITeamDataProvider teamProvider, string teamName, string description)
    {
        var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);

        if (teams.Any(t => t.Name == teamName))
            return;

        await teamProvider.AddAsync(new Team { Name = teamName, Description = description, SpaceId = 0, IsBuiltIn = true }).ConfigureAwait(false);

        Log.Information("Seeded default team {TeamName}", teamName);
    }
}
