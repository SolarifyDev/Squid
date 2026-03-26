using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;

namespace Squid.Core.Services.DataSeeding;

public class DefaultSpaceSeeder : IDataSeeder
{
    public int Order => 400;

    public async Task SeedAsync(ILifetimeScope scope)
    {
        var repository = scope.Resolve<IRepository>();
        var unitOfWork = scope.Resolve<IUnitOfWork>();
        var teamProvider = scope.Resolve<ITeamDataProvider>();
        var roleProvider = scope.Resolve<IUserRoleDataProvider>();
        var scopedRoleProvider = scope.Resolve<IScopedUserRoleDataProvider>();

        await SeedDefaultSpaceAsync(repository, unitOfWork).ConfigureAwait(false);
        await AssignAdministratorsSpaceOwnerAsync(teamProvider, roleProvider, scopedRoleProvider, repository, unitOfWork).ConfigureAwait(false);

        Log.Information("Default space seeding complete");
    }

    private static async Task SeedDefaultSpaceAsync(IRepository repository, IUnitOfWork unitOfWork)
    {
        var existing = await repository.FirstOrDefaultAsync<Space>(s => s.IsDefault).ConfigureAwait(false);

        if (existing != null) return;

        try
        {
            var space = new Space
            {
                Name = "Default",
                Slug = "default",
                Description = "",
                IsDefault = true,
                Json = "{}",
                TaskQueueStopped = false,
                IsPrivate = false
            };

            await repository.InsertAsync(space).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            Log.Information("Seeded default space {SpaceName}", space.Name);
        }
        catch (Exception ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            Log.Debug("Default space was already created by another instance");
        }
    }

    private static async Task AssignAdministratorsSpaceOwnerAsync(ITeamDataProvider teamProvider, IUserRoleDataProvider roleProvider, IScopedUserRoleDataProvider scopedRoleProvider, IRepository repository, IUnitOfWork unitOfWork)
    {
        var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
        var adminTeam = teams.FirstOrDefault(t => t.Name == "Squid Administrators");

        if (adminTeam == null) return;

        var spaceOwnerRole = await roleProvider.GetByNameAsync(BuiltInRoles.SpaceOwner.Name).ConfigureAwait(false);

        if (spaceOwnerRole == null) return;

        var defaultSpace = await repository.FirstOrDefaultAsync<Space>(s => s.IsDefault).ConfigureAwait(false);

        if (defaultSpace == null) return;

        var existingScopedRoles = await scopedRoleProvider.GetByTeamIdsAsync(new List<int> { adminTeam.Id }).ConfigureAwait(false);
        var hasSpaceOwner = existingScopedRoles.Any(sr => sr.UserRoleId == spaceOwnerRole.Id && sr.SpaceId == defaultSpace.Id);

        if (!hasSpaceOwner)
        {
            await scopedRoleProvider.AddAsync(new ScopedUserRole { TeamId = adminTeam.Id, UserRoleId = spaceOwnerRole.Id, SpaceId = defaultSpace.Id }).ConfigureAwait(false);

            Log.Information("Assigned Space Owner role to Squid Administrators in Default Space");
        }

        if (defaultSpace.OwnerTeamId != adminTeam.Id)
        {
            defaultSpace.OwnerTeamId = adminTeam.Id;

            await repository.UpdateAsync(defaultSpace).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            Log.Information("Set Default Space owner team to Squid Administrators");
        }
    }
}
