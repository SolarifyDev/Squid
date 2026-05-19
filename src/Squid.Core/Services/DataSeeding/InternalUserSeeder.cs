using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Constants;
using Serilog;

namespace Squid.Core.Services.DataSeeding;

/// <summary>
/// Seeds the InternalUser account (<c>CurrentUsers.InternalUser.Id</c> = 8888) plus the
/// "Internal Service Accounts" team that grants it the <c>SystemServiceAccount</c> role.
/// Together this lets the Tentacle install-script bootstrap API key (owned by user 8888)
/// pass the <c>MachineCreate</c> permission check at register time.
///
/// <para><b>Why an Id-pinned user</b>: <c>CurrentUsers.InternalUser.Id</c> is a compile-
/// time constant (8888) referenced by <c>SquidDbContext</c> audit columns (default value
/// for <c>created_by</c> / <c>last_modified_by</c>), the API-key authentication handler,
/// and the install-script generator. All of these would silently break if the user row
/// didn't exist or had a different Id.</para>
///
/// <para><b>Order = 350</b>: must run AFTER <see cref="BuiltInRoleSeeder"/> (Order=100,
/// creates the SystemServiceAccount role row), AFTER <see cref="BuiltInTeamSeeder"/>
/// (Order=200), and AFTER <see cref="AdminUserSeeder"/> (Order=300, so the audit
/// columns can reference 8888 without violating audit ordering).</para>
///
/// <para><b>Idempotent</b>: every step uses "exists check + create-if-missing" so this
/// seeder can run on every startup without duplicating rows. Pre-existing operator
/// modifications (e.g. additional roles assigned to the Internal Service Accounts team)
/// are preserved.</para>
/// </summary>
public class InternalUserSeeder : IDataSeeder
{
    private const string InternalServiceAccountsTeamName = "Internal Service Accounts";

    private const string InternalServiceAccountsTeamDescription =
        "Reserved team for the InternalUser system account (Tentacle install bootstrap, automation). " +
        "Do NOT add human users -- assigning here silently grants them SystemServiceAccount permissions.";

    public int Order => 350;

    public async Task SeedAsync(ILifetimeScope scope)
    {
        var repository = scope.Resolve<IRepository>();
        var unitOfWork = scope.Resolve<IUnitOfWork>();
        var dbContext = scope.Resolve<SquidDbContext>();
        var teamProvider = scope.Resolve<ITeamDataProvider>();
        var roleProvider = scope.Resolve<IUserRoleDataProvider>();
        var scopedRoleProvider = scope.Resolve<IScopedUserRoleDataProvider>();

        await EnsureInternalUserAccountAsync(repository, dbContext).ConfigureAwait(false);
        var team = await EnsureInternalServiceAccountsTeamAsync(teamProvider).ConfigureAwait(false);
        await EnsureTeamHasSystemServiceAccountRoleAsync(roleProvider, scopedRoleProvider, team).ConfigureAwait(false);
        await EnsureInternalUserInTeamAsync(teamProvider, team).ConfigureAwait(false);

        Log.Information("Internal user seeding complete");
    }

    /// <summary>
    /// Inserts a <c>user_account</c> row with the Id-pinned sentinel
    /// (<c>CurrentUsers.InternalUser.Id</c>) using raw SQL — EF's Identity convention
    /// rejects explicit Id values for auto-generated columns, so going through
    /// <c>IRepository.InsertAsync</c> would either silently re-assign a new Id or
    /// throw. The raw INSERT lets PostgreSQL accept the explicit Id (the sequence
    /// default is overridden by the explicit value column).
    /// </summary>
    private static async Task EnsureInternalUserAccountAsync(IRepository repository, SquidDbContext dbContext)
    {
        var existing = await repository.FirstOrDefaultAsync<UserAccount>(x => x.Id == CurrentUsers.InternalUser.Id).ConfigureAwait(false);

        if (existing != null)
        {
            if (!existing.IsSystem) Log.Warning("Internal user (id={Id}) exists but IsSystem=false -- it should be true; UI will show it as a regular user", CurrentUsers.InternalUser.Id);
            return;
        }

        const string sql = @"
            INSERT INTO user_account
                (id, user_name, normalized_user_name, display_name, password_hash,
                 is_disabled, is_system, must_change_password,
                 created_date, last_modified_date, created_by, last_modified_by)
            VALUES
                ({0}, {1}, {2}, {3}, '',
                 false, true, false,
                 {4}, {4}, {0}, {0})
            ON CONFLICT (id) DO NOTHING";

        var now = DateTimeOffset.UtcNow;
        var rows = await dbContext.Database.ExecuteSqlRawAsync(sql,
            CurrentUsers.InternalUser.Id, CurrentUsers.InternalUser.Name, CurrentUsers.InternalUser.Name.ToUpperInvariant(), CurrentUsers.InternalUser.DisplayName, now).ConfigureAwait(false);

        Log.Information("Seeded internal user (id={Id}, rowsAffected={Rows})", CurrentUsers.InternalUser.Id, rows);
    }

    /// <summary>
    /// Creates the "Internal Service Accounts" team in space 0 (system / cross-space)
    /// if absent. Idempotent: returns the existing row when already present.
    /// </summary>
    private static async Task<Team> EnsureInternalServiceAccountsTeamAsync(ITeamDataProvider teamProvider)
    {
        var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
        var existing = teams.FirstOrDefault(t => t.Name == InternalServiceAccountsTeamName);

        if (existing != null) return existing;

        var team = new Team { Name = InternalServiceAccountsTeamName, Description = InternalServiceAccountsTeamDescription, SpaceId = 0, IsBuiltIn = true };
        await teamProvider.AddAsync(team).ConfigureAwait(false);
        Log.Information("Seeded team {TeamName}", InternalServiceAccountsTeamName);

        return team;
    }

    /// <summary>
    /// Assigns the <c>SystemServiceAccount</c> role to the team with <c>SpaceId=null</c>
    /// (applies in every space, matching how <c>BuiltInTeamSeeder</c> assigns the
    /// SystemAdministrator role to Squid Administrators). Idempotent.
    /// </summary>
    private static async Task EnsureTeamHasSystemServiceAccountRoleAsync(IUserRoleDataProvider roleProvider, IScopedUserRoleDataProvider scopedRoleProvider, Team team)
    {
        var role = await roleProvider.GetByNameAsync(BuiltInRoles.SystemServiceAccount.Name).ConfigureAwait(false);

        if (role == null)
            throw new InvalidOperationException($"Built-in role '{BuiltInRoles.SystemServiceAccount.Name}' missing; ensure {nameof(BuiltInRoleSeeder)} ran first (Order=100).");

        var existing = await scopedRoleProvider.GetByTeamIdsAsync(new List<int> { team.Id }).ConfigureAwait(false);

        if (existing.Any(sr => sr.UserRoleId == role.Id)) return;

        await scopedRoleProvider.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = null }).ConfigureAwait(false);
        Log.Information("Assigned role {RoleName} to team {TeamName} (cross-space)", BuiltInRoles.SystemServiceAccount.Name, InternalServiceAccountsTeamName);
    }

    /// <summary>
    /// Adds the InternalUser to the team. Idempotent — checks membership first to
    /// avoid violating the team_member primary-key constraint on re-run.
    /// </summary>
    private static async Task EnsureInternalUserInTeamAsync(ITeamDataProvider teamProvider, Team team)
    {
        var members = await teamProvider.GetMembersByTeamIdAsync(team.Id).ConfigureAwait(false);

        if (members.Any(m => m.UserId == CurrentUsers.InternalUser.Id)) return;

        await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = CurrentUsers.InternalUser.Id }).ConfigureAwait(false);
        Log.Information("Added InternalUser (id={UserId}) to team {TeamName}", CurrentUsers.InternalUser.Id, InternalServiceAccountsTeamName);
    }
}
