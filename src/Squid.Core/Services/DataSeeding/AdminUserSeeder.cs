using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Teams;
using Squid.Core.Settings.Setup;
using Squid.Core.Setup;
using Squid.Core.Setup.Secrets;
using Squid.Message.Constants;
using Squid.Message.Models.Setup;

namespace Squid.Core.Services.DataSeeding;

public class AdminUserSeeder : IDataSeeder
{
    public const string InitialPasswordFileName = "initial-admin-password";

    private readonly BootstrapAdminSetting _bootstrapAdmin;
    private readonly SetupAdminSetting _setupAdmin;
    private readonly DataDirectorySetting _dataDirectory;

    // Settings are constructor-injected on purpose: seeders run inside an IStartable during container
    // build, and resolving a singleton from the scope after the first await deadlocks on the root
    // scope's shared-instance lock held by the blocked build thread.
    public AdminUserSeeder(BootstrapAdminSetting bootstrapAdmin, SetupAdminSetting setupAdmin, DataDirectorySetting dataDirectory)
    {
        _bootstrapAdmin = bootstrapAdmin;
        _setupAdmin = setupAdmin;
        _dataDirectory = dataDirectory;
    }

    public int Order => 300;

    public async Task SeedAsync(ILifetimeScope scope)
    {
        var repository = scope.Resolve<IRepository>();
        var unitOfWork = scope.Resolve<IUnitOfWork>();
        var teamProvider = scope.Resolve<ITeamDataProvider>();
        var initialAdmin = ResolveInitialAdmin(_bootstrapAdmin, _setupAdmin, _dataDirectory);

        await SeedAdminUserAsync(repository, unitOfWork, teamProvider, initialAdmin).ConfigureAwait(false);

        Log.Information("Admin user seeding complete");
    }

    /// <summary>
    /// Who the first administrator is and how their password is set. In order: the account chosen
    /// in the setup wizard (name + hash, as a pair), a plain password passed as a start parameter,
    /// a random one for a self-contained install (handed over through a file in the data directory
    /// — never a well-known default there), else the historical default every pre-existing
    /// deployment was seeded with.
    /// </summary>
    public static InitialAdmin ResolveInitialAdmin(BootstrapAdminSetting bootstrap, SetupAdminSetting setupAdmin, DataDirectorySetting dataDirectory)
    {
        if (setupAdmin.IsConfigured) return new InitialAdmin(setupAdmin.UserName.Trim(), null, setupAdmin.PasswordHash, false, null);

        var userName = string.IsNullOrWhiteSpace(bootstrap.UserName) ? CurrentUsers.AdminUser.UserName : bootstrap.UserName.Trim();

        if (!string.IsNullOrWhiteSpace(bootstrap.Password)) return new InitialAdmin(userName, bootstrap.Password, null, false, null);
        if (dataDirectory.IsConfigured) return new InitialAdmin(userName, RandomSecret.Alphanumeric(20), null, true, dataDirectory.PathOf(InitialPasswordFileName));

        return new InitialAdmin(userName, CurrentUsers.AdminUser.DefaultPassword, null, true, null);
    }

    private static async Task SeedAdminUserAsync(IRepository repository, IUnitOfWork unitOfWork, ITeamDataProvider teamProvider, InitialAdmin initialAdmin)
    {
        var normalizedUserName = initialAdmin.UserName.ToUpperInvariant();
        var existing = await repository.FirstOrDefaultAsync<UserAccount>(x => x.NormalizedUserName == normalizedUserName).ConfigureAwait(false);

        if (existing != null)
        {
            await EnsureAdministratorTeamsAsync(teamProvider, existing.Id).ConfigureAwait(false);
            return;
        }

        if (await HasAnyHumanUserAsync(repository).ConfigureAwait(false))
        {
            Log.Information("Not creating administrator {UserName}: the install already has user accounts", initialAdmin.UserName);
            return;
        }

        await CreateAdministratorAsync(repository, unitOfWork, teamProvider, initialAdmin).ConfigureAwait(false);
    }

    /// <summary>
    /// The first administrator is created only on a fresh install. Once anyone exists, a missing
    /// administrator was removed or renamed on purpose, and recreating it with the bootstrap (or
    /// historical default) password would silently re-open that account.
    /// </summary>
    private static async Task<bool> HasAnyHumanUserAsync(IRepository repository)
    {
        var anyone = await repository.FirstOrDefaultAsync<UserAccount>(x => !x.IsSystem).ConfigureAwait(false);

        return anyone != null;
    }

    private static async Task CreateAdministratorAsync(IRepository repository, IUnitOfWork unitOfWork, ITeamDataProvider teamProvider, InitialAdmin initialAdmin)
    {
        try
        {
            var user = BuildAdministrator(initialAdmin);

            // Hand the generated password over BEFORE the account exists: if the file cannot be
            // written the start fails and is retried, instead of leaving an account nobody can open.
            HandOverGeneratedPassword(initialAdmin);

            await repository.InsertAsync(user).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            await EnsureAdministratorTeamsAsync(teamProvider, user.Id).ConfigureAwait(false);

            Log.Information("Seeded admin user {UserName}", initialAdmin.UserName);
        }
        catch (Exception ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            Log.Debug("Admin user was already created by another instance");
        }
    }

    private static UserAccount BuildAdministrator(InitialAdmin initialAdmin)
    {
        var user = new UserAccount
        {
            UserName = initialAdmin.UserName,
            NormalizedUserName = initialAdmin.UserName.ToUpperInvariant(),
            DisplayName = CurrentUsers.AdminUser.DisplayName,
            IsDisabled = false,
            IsSystem = false,
            MustChangePassword = initialAdmin.MustChangePassword,
            CreatedDate = DateTime.UtcNow,
            LastModifiedDate = DateTime.UtcNow
        };

        user.PasswordHash = initialAdmin.PasswordHash ?? new PasswordHasher<UserAccount>().HashPassword(user, initialAdmin.Password);

        return user;
    }

    private static void HandOverGeneratedPassword(InitialAdmin initialAdmin)
    {
        if (initialAdmin.GeneratedPasswordFile == null) return;

        OwnerOnlyFile.WriteAtomically(initialAdmin.GeneratedPasswordFile, initialAdmin.Password + Environment.NewLine);

        // The password itself is deliberately NOT logged: container logs are commonly shipped to
        // aggregators readable by far more people than can read the data volume.
        Log.Warning("Created administrator {UserName} with a generated password. Read it with: docker exec <container> cat {PasswordFile} — and change it after the first sign-in", initialAdmin.UserName, initialAdmin.GeneratedPasswordFile);
    }

    private static async Task EnsureAdministratorTeamsAsync(ITeamDataProvider teamProvider, int userId)
    {
        await AddUserToTeamAsync(teamProvider, userId, "Squid Administrators").ConfigureAwait(false);
        await AddUserToTeamAsync(teamProvider, userId, "Everyone").ConfigureAwait(false);
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
