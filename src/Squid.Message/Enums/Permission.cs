namespace Squid.Message.Enums;

public enum PermissionScope
{
    SpaceOnly,
    SystemOnly,
    Mixed
}

[AttributeUsage(AttributeTargets.Field)]
public class PermissionScopeAttribute(PermissionScope scope) : Attribute
{
    public PermissionScope Scope { get; } = scope;
}

public enum Permission
{
    // Project
    [PermissionScope(PermissionScope.SpaceOnly)] ProjectView,
    [PermissionScope(PermissionScope.SpaceOnly)] ProjectCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] ProjectEdit,
    [PermissionScope(PermissionScope.SpaceOnly)] ProjectDelete,

    // Process
    [PermissionScope(PermissionScope.SpaceOnly)] ProcessView,
    [PermissionScope(PermissionScope.SpaceOnly)] ProcessEdit,

    // Variable
    [PermissionScope(PermissionScope.SpaceOnly)] VariableView,
    [PermissionScope(PermissionScope.SpaceOnly)] VariableEdit,

    // Release
    [PermissionScope(PermissionScope.SpaceOnly)] ReleaseView,
    [PermissionScope(PermissionScope.SpaceOnly)] ReleaseCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] ReleaseEdit,
    [PermissionScope(PermissionScope.SpaceOnly)] ReleaseDelete,

    // Deployment
    [PermissionScope(PermissionScope.SpaceOnly)] DeploymentView,
    [PermissionScope(PermissionScope.SpaceOnly)] DeploymentCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] DeploymentDelete,

    // Environment
    [PermissionScope(PermissionScope.SpaceOnly)] EnvironmentView,
    [PermissionScope(PermissionScope.SpaceOnly)] EnvironmentCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] EnvironmentEdit,
    [PermissionScope(PermissionScope.SpaceOnly)] EnvironmentDelete,

    // Machine
    [PermissionScope(PermissionScope.SpaceOnly)] MachineView,
    [PermissionScope(PermissionScope.SpaceOnly)] MachineCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] MachineEdit,
    [PermissionScope(PermissionScope.SpaceOnly)] MachineDelete,

    // Account
    [PermissionScope(PermissionScope.SpaceOnly)] AccountView,
    [PermissionScope(PermissionScope.SpaceOnly)] AccountCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] AccountEdit,
    [PermissionScope(PermissionScope.SpaceOnly)] AccountDelete,

    // Feed
    [PermissionScope(PermissionScope.SpaceOnly)] FeedView,
    [PermissionScope(PermissionScope.SpaceOnly)] FeedEdit,

    // Lifecycle
    [PermissionScope(PermissionScope.SpaceOnly)] LifecycleView,
    [PermissionScope(PermissionScope.SpaceOnly)] LifecycleCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] LifecycleEdit,
    [PermissionScope(PermissionScope.SpaceOnly)] LifecycleDelete,

    // Channel
    [PermissionScope(PermissionScope.SpaceOnly)] ChannelView,
    [PermissionScope(PermissionScope.SpaceOnly)] ChannelCreate,
    [PermissionScope(PermissionScope.SpaceOnly)] ChannelEdit,
    [PermissionScope(PermissionScope.SpaceOnly)] ChannelDelete,

    // Task
    [PermissionScope(PermissionScope.Mixed)] TaskView,
    [PermissionScope(PermissionScope.Mixed)] TaskCreate,
    [PermissionScope(PermissionScope.Mixed)] TaskCancel,

    // Interruption
    [PermissionScope(PermissionScope.SpaceOnly)] InterruptionView,
    [PermissionScope(PermissionScope.SpaceOnly)] InterruptionSubmit,
    // Team
    [PermissionScope(PermissionScope.Mixed)] TeamView,
    [PermissionScope(PermissionScope.Mixed)] TeamCreate,
    [PermissionScope(PermissionScope.Mixed)] TeamEdit,
    [PermissionScope(PermissionScope.Mixed)] TeamDelete,

    // User & Role (system-level)
    [PermissionScope(PermissionScope.SystemOnly)] UserView,
    [PermissionScope(PermissionScope.SystemOnly)] UserEdit,
    [PermissionScope(PermissionScope.SystemOnly)] UserRoleView,
    [PermissionScope(PermissionScope.SystemOnly)] UserRoleEdit,

    // Space
    [PermissionScope(PermissionScope.SystemOnly)] SpaceView,
    [PermissionScope(PermissionScope.SystemOnly)] SpaceCreate,
    [PermissionScope(PermissionScope.SystemOnly)] SpaceEdit,
    [PermissionScope(PermissionScope.SystemOnly)] SpaceDelete,

    // System
    [PermissionScope(PermissionScope.SystemOnly)] AdministerSystem,
}
