using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Message.Enums;

namespace Squid.Core.Services.DataSeeding;

public class BuiltInRoleSeeder : IDataSeeder
{
    public int Order => 100;

    public async Task SeedAsync(ILifetimeScope scope)
    {
        var roleProvider = scope.Resolve<IUserRoleDataProvider>();

        foreach (var definition in BuiltInRoles.All)
        {
            await SeedRoleAsync(roleProvider, definition).ConfigureAwait(false);
        }

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
