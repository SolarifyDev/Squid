using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Teams;
using Squid.Message.Constants;
using Squid.Message.Enums;

namespace Squid.Core.Services.Authorization;

public class BuiltInRoleSeeder : IStartable
{
    private readonly ILifetimeScope _scope;

    public BuiltInRoleSeeder(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public void Start()
    {
        try
        {
            SeedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Built-in role seeding failed — will retry on next startup");
        }
    }

    private async Task SeedAsync()
    {
        await using var scope = _scope.BeginLifetimeScope();

        var roleProvider = scope.Resolve<IUserRoleDataProvider>();
        var teamProvider = scope.Resolve<ITeamDataProvider>();

        foreach (var definition in BuiltInRoles.All)
        {
            await SeedRoleAsync(roleProvider, definition).ConfigureAwait(false);
        }

        await SeedDefaultTeamsAsync(teamProvider, roleProvider, scope.Resolve<IScopedUserRoleDataProvider>()).ConfigureAwait(false);
        await SeedAdminUserAsync(scope.Resolve<IRepository>(), scope.Resolve<IUnitOfWork>(), teamProvider).ConfigureAwait(false);

        Log.Information("Built-in role seeding complete");
    }

    private static async Task SeedRoleAsync(IUserRoleDataProvider roleProvider, BuiltInRoleDefinition definition)
    {
        var existing = await roleProvider.GetByNameAsync(definition.Name).ConfigureAwait(false);

        if (existing == null)
        {
            try
            {
                var role = new UserRole { Name = definition.Name, Description = definition.Description, IsBuiltIn = true };
                await roleProvider.AddAsync(role).ConfigureAwait(false);
                await roleProvider.SetPermissionsAsync(role.Id, definition.Permissions.Select(p => p.ToString()).ToList()).ConfigureAwait(false);

                Log.Information("Seeded built-in role {RoleName} with {Count} permissions", definition.Name, definition.Permissions.Count);
            }
            catch (Exception ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                Log.Debug("Built-in role {RoleName} was already created by another instance", definition.Name);

                existing = await roleProvider.GetByNameAsync(definition.Name).ConfigureAwait(false);

                if (existing != null)
                    await roleProvider.SetPermissionsAsync(existing.Id, definition.Permissions.Select(p => p.ToString()).ToList()).ConfigureAwait(false);
            }
        }
        else
        {
            await roleProvider.SetPermissionsAsync(existing.Id, definition.Permissions.Select(p => p.ToString()).ToList()).ConfigureAwait(false);

            Log.Debug("Updated permissions for built-in role {RoleName}", definition.Name);
        }
    }

    private static async Task SeedDefaultTeamsAsync(ITeamDataProvider teamProvider, IUserRoleDataProvider roleProvider, IScopedUserRoleDataProvider scopedRoleProvider)
    {
        await SeedTeamWithRoleAsync(teamProvider, roleProvider, scopedRoleProvider, "Squid Administrators", "Built-in administrators team", BuiltInRoles.SystemAdministrator.Name).ConfigureAwait(false);
        await SeedTeamAsync(teamProvider, "Everyone", "All users").ConfigureAwait(false);
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

public class BuiltInRoleDefinition
{
    public string Name { get; init; }
    public string Description { get; init; }
    public List<Permission> Permissions { get; init; }
}

public static class BuiltInRoles
{
    public static readonly BuiltInRoleDefinition SystemAdministrator = new()
    {
        Name = "System Administrator",
        Description = "Can manage all system-level settings and has full access to all spaces",
        Permissions = new List<Permission>
        {
            Permission.AdministerSystem,
            Permission.SpaceView, Permission.SpaceCreate, Permission.SpaceEdit, Permission.SpaceDelete,
            Permission.UserView, Permission.UserEdit,
            Permission.UserRoleView, Permission.UserRoleEdit,
            Permission.TeamView, Permission.TeamCreate, Permission.TeamEdit, Permission.TeamDelete,
            Permission.TaskView, Permission.TaskCreate, Permission.TaskCancel,
        }
    };

    public static readonly BuiltInRoleDefinition SpaceOwner = new()
    {
        Name = "Space Owner",
        Description = "Has full access to all resources within a space",
        Permissions = Enum.GetValues<Permission>().Where(p => p.CanApplyAtSpaceLevel()).ToList()
    };

    public static readonly BuiltInRoleDefinition ProjectDeployer = new()
    {
        Name = "Project Deployer",
        Description = "Can deploy releases and manage deployments within a space",
        Permissions = new List<Permission>
        {
            Permission.ProjectView, Permission.ProcessView, Permission.ReleaseView,
            Permission.DeploymentView, Permission.DeploymentCreate,
            Permission.EnvironmentView, Permission.MachineView,
            Permission.VariableView, Permission.AccountView, Permission.FeedView,
            Permission.LifecycleView, Permission.ChannelView,
            Permission.TaskView, Permission.TaskCreate, Permission.TaskCancel,
            Permission.InterruptionView, Permission.InterruptionSubmit,
        }
    };

    public static readonly BuiltInRoleDefinition ProjectContributor = new()
    {
        Name = "Project Contributor",
        Description = "Can edit projects, processes, variables, and channels within a space",
        Permissions = new List<Permission>
        {
            Permission.ProjectView, Permission.ProjectEdit,
            Permission.ProcessView, Permission.ProcessEdit,
            Permission.VariableView, Permission.VariableEdit,
            Permission.ReleaseView, Permission.ReleaseCreate,
            Permission.ChannelView, Permission.ChannelCreate, Permission.ChannelEdit, Permission.ChannelDelete,
            Permission.EnvironmentView, Permission.MachineView,
            Permission.LifecycleView, Permission.FeedView,
            Permission.DeploymentView, Permission.TaskView,
        }
    };

    public static readonly BuiltInRoleDefinition ProjectViewer = new()
    {
        Name = "Project Viewer",
        Description = "Has read-only access to projects and related resources",
        Permissions = new List<Permission>
        {
            Permission.ProjectView, Permission.ProcessView, Permission.ReleaseView,
            Permission.DeploymentView, Permission.EnvironmentView,
            Permission.VariableView, Permission.TaskView,
            Permission.LifecycleView, Permission.ChannelView,
            Permission.MachineView, Permission.FeedView,
        }
    };

    public static readonly BuiltInRoleDefinition EnvironmentManager = new()
    {
        Name = "Environment Manager",
        Description = "Can manage environments, machines, and accounts",
        Permissions = new List<Permission>
        {
            Permission.EnvironmentView, Permission.EnvironmentCreate, Permission.EnvironmentEdit, Permission.EnvironmentDelete,
            Permission.MachineView, Permission.MachineCreate, Permission.MachineEdit, Permission.MachineDelete,
            Permission.AccountView, Permission.AccountCreate, Permission.AccountEdit, Permission.AccountDelete,
            Permission.TaskView, Permission.TaskCreate, Permission.TaskCancel,
        }
    };

    public static readonly List<BuiltInRoleDefinition> All = new()
    {
        SystemAdministrator,
        SpaceOwner,
        ProjectDeployer,
        ProjectContributor,
        ProjectViewer,
        EnvironmentManager,
    };
}
