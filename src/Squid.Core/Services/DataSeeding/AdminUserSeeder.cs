using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Teams;
using Squid.Message.Constants;

namespace Squid.Core.Services.DataSeeding;

public class AdminUserSeeder : IDataSeeder
{
    public int Order => 300;

    public async Task SeedAsync(ILifetimeScope scope)
    {
        var repository = scope.Resolve<IRepository>();
        var unitOfWork = scope.Resolve<IUnitOfWork>();
        var teamProvider = scope.Resolve<ITeamDataProvider>();

        await SeedAdminUserAsync(repository, unitOfWork, teamProvider).ConfigureAwait(false);

        Log.Information("Admin user seeding complete");
    }

    private static async Task SeedAdminUserAsync(IRepository repository, IUnitOfWork unitOfWork, ITeamDataProvider teamProvider)
    {
        var normalizedUserName = CurrentUsers.AdminUser.UserName.ToUpperInvariant();
        var existing = await repository.FirstOrDefaultAsync<UserAccount>(x => x.NormalizedUserName == normalizedUserName).ConfigureAwait(false);

        if (existing == null)
        {
            try
            {
                var passwordHasher = new PasswordHasher<UserAccount>();
                var user = new UserAccount
                {
                    UserName = CurrentUsers.AdminUser.UserName,
                    NormalizedUserName = normalizedUserName,
                    DisplayName = CurrentUsers.AdminUser.DisplayName,
                    IsDisabled = false,
                    IsSystem = false,
                    MustChangePassword = true,
                    CreatedDate = DateTime.UtcNow,
                    LastModifiedDate = DateTime.UtcNow
                };

                user.PasswordHash = passwordHasher.HashPassword(user, CurrentUsers.AdminUser.DefaultPassword);

                await repository.InsertAsync(user).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

                await AddUserToTeamAsync(teamProvider, user.Id, "Squid Administrators").ConfigureAwait(false);
                await AddUserToTeamAsync(teamProvider, user.Id, "Everyone").ConfigureAwait(false);

                Log.Information("Seeded admin user {UserName}", CurrentUsers.AdminUser.UserName);
            }
            catch (Exception ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                Log.Debug("Admin user was already created by another instance");
            }
        }
        else
        {
            await AddUserToTeamAsync(teamProvider, existing.Id, "Squid Administrators").ConfigureAwait(false);
            await AddUserToTeamAsync(teamProvider, existing.Id, "Everyone").ConfigureAwait(false);
        }
    }

    private static async Task AddUserToTeamAsync(ITeamDataProvider teamProvider, int userId, string teamName)
    {
        var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
        var team = teams.FirstOrDefault(t => t.Name == teamName);

        if (team == null) return;

        var members = await teamProvider.GetMembersByTeamIdAsync(team.Id).ConfigureAwait(false);

        if (members.Any(m => m.UserId == userId)) return;

        await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId }).ConfigureAwait(false);
    }
}
